using System;
using System.Collections;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using Mazandasht.Desktop.Security;
using Mazandasht.Desktop.Services;

namespace Mazandasht.Desktop.Import
{
    public sealed class ImportCommitResult
    {
        public string BatchId { get; set; }
        public string ProfileCode { get; set; }
        public string Status { get; set; }
        public int TotalRows { get; set; }
        public int ImportedRows { get; set; }
        public int RejectedRows { get; set; }
        public int DuplicateRows { get; set; }
        public bool DryRun { get; set; }
        public List<string> Errors { get; set; }
    }

    /// <summary>
    /// Commits a staged import batch into the operational tables.
    ///
    /// Guarantees:
    ///  - nothing is written unless the batch is in PREVIEW state and the caller
    ///    holds the documents.create permission;
    ///  - every row is validated before it is written, and a rejected row is
    ///    recorded with its reason instead of aborting the whole batch;
    ///  - a row whose source hash was already imported is marked DUPLICATE;
    ///  - operational rows become DRAFT documents with requires_journal=0 and
    ///    requires_stock_posting=0, so a commit has no financial or inventory
    ///    effect until a module engine posts an approved document;
    ///  - the batch counters always equal the rows actually written;
    ///  - no raw source payload is ever logged.
    /// </summary>
    public static class ImportCommitService
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Mazandasht.ImportStage.v1");

        private const string SqlBatchLoad =
            @"SELECT id,source_file_name,import_profile,status,total_rows,accepted_rows,rejected_rows,
                     column_mapping_json,source_sheet_name
              FROM import_batches WHERE id=@id;";
        private const string SqlRows =
            @"SELECT id,source_row_number,external_key,row_status,target_entity,target_id,
                     raw_row_json,validation_errors_json,source_row_sha256,raw_sensitive_enc
              FROM import_rows WHERE import_batch_id=@batch ORDER BY source_row_number;";
        private const string SqlRowUpdate =
            @"UPDATE import_rows SET external_key=@key,row_status=@status,target_entity=@entity,
                     target_id=@target,validation_errors_json=@errors
              WHERE id=@id;";
        private const string SqlBatchUpdate =
            @"UPDATE import_batches SET status=@status,accepted_rows=@accepted,rejected_rows=@rejected,
                     committed_at_utc=@committed WHERE id=@id;";
        private const string SqlDocumentInsert =
            @"INSERT INTO documents(id,document_type,fiscal_year,document_number,date_iso,status,
                    requires_journal,requires_stock_posting,party_account_id,reference_number,notes,created_by)
              VALUES(@id,@type,@fiscal,'',@date,'DRAFT',0,0,@party,@reference,@notes,@user);";
        private const string SqlDocumentLineInsert =
            @"INSERT INTO document_lines(id,document_id,line_number,product_id,packaging_type_id,
                    quantity_g,quantity_count,unit_price_irr_per_kg,amount_irr,description)
              VALUES(@id,@document,1,@product,@packaging,@quantityG,@quantityCount,@unitPrice,@amount,@description);";
        private const string SqlStatusHistoryInsert =
            @"INSERT INTO document_status_history(id,document_id,old_status,new_status,changed_by,reason)
              VALUES(@id,@document,NULL,'DRAFT',@user,@reason);";
        private const string SqlDuplicateCheck =
            @"SELECT COUNT(*) FROM import_rows WHERE source_row_sha256=@hash AND row_status='IMPORTED';";
        private const string SqlReferenceDocumentCheck =
            @"SELECT COUNT(*) FROM documents WHERE document_type=@type AND reference_number=@reference;";
        private const string SqlPermissionCheck =
            @"SELECT COUNT(*) FROM users u
              JOIN roles r ON r.id=u.role_id
              JOIN role_permissions rp ON rp.role_id=r.id
              WHERE u.id=@user AND u.is_active=1 AND r.is_active=1
                AND rp.permission_code='documents.create' AND rp.granted=1;";
        private const string SqlAuditInsert =
            @"INSERT INTO audit_log(id,actor_user_id,action_code,entity_type,entity_id,after_json,machine_name)
              VALUES(@id,@actor,@action,@entityType,@entityId,@after,@machine);";

        public static ImportCommitResult Validate(string databasePath, string batchId, string userId)
        {
            return Commit(databasePath, batchId, userId, true);
        }

        public static ImportCommitResult Commit(string databasePath, string batchId, string userId)
        {
            return Commit(databasePath, batchId, userId, false);
        }

        private static ImportCommitResult Commit(string databasePath, string batchId, string userId,
            bool dryRun)
        {
            var result = new ImportCommitResult {
                BatchId = batchId, Status = "FAILED", Errors = new List<string>(), DryRun = dryRun
            };

            if (String.IsNullOrWhiteSpace(batchId))
                throw new ArgumentException("شناسه بسته ورود اطلاعات الزامی است.");

            var profileCode = Convert.ToString(LookupBatchProfile(databasePath, batchId));
            var target = ImportTargetCatalog.Find(profileCode);
            if (target == null)
                throw new InvalidOperationException("پروفایل این بسته در فهرست پروفایل‌های قابل انتقال نیست.");
            result.ProfileCode = target.ProfileCode;

            using (var connection = Open(databasePath))
            using (var transaction = connection.BeginTransaction())
            {
                RequireCommitPermission(connection, transaction, userId);

                var batch = LoadBatch(connection, transaction, batchId);
                if (!String.Equals(batch.Status, "PREVIEW", StringComparison.Ordinal))
                    throw new InvalidOperationException("این بسته در وضعیت «پیش‌نمایش» نیست و قابل انتقال نیست.");

                var mapping = ReadMapping(batch.ColumnMappingJson);
                var rows = LoadRows(connection, transaction, batchId);
                result.TotalRows = rows.Count;

                var resolver = new ReferenceResolver(connection, transaction);
                foreach (var row in rows)
                {
                    string error = null;
                    Dictionary<string, string> values = null;
                    try
                    {
                        values = DecodeRow(row, mapping);
                        error = ValidateRow(target, values);
                    }
                    catch (Exception ex)
                    {
                        error = ex.Message;
                    }

                    if (error != null)
                    {
                        MarkRow(connection, transaction, row, "REJECTED", null, null, error, dryRun);
                        result.RejectedRows++;
                        result.Errors.Add("ردیف " + row.SourceRowNumber + ": " + error);
                        continue;
                    }

                    if (RowAlreadyImported(connection, transaction, row.SourceRowSha256))
                    {
                        MarkRow(connection, transaction, row, "DUPLICATE", null, null, null, dryRun);
                        result.DuplicateRows++;
                        continue;
                    }

                    string entityType;
                    string entityId;
                    try
                    {
                        if (target.Kind == ImportTargetKind.CatalogParty)
                        {
                            entityId = ImportParty(connection, transaction, userId, target, values, resolver);
                            entityType = "PARTY";
                        }
                        else if (target.Kind == ImportTargetKind.CatalogProduct)
                        {
                            entityId = ImportProduct(connection, transaction, userId, target, values, resolver);
                            entityType = "PRODUCT";
                        }
                        else
                        {
                            entityId = ImportDocument(connection, transaction, databasePath, userId,
                                target, values, resolver);
                            entityType = "DOCUMENT";
                        }
                    }
                    catch (Exception ex)
                    {
                        MarkRow(connection, transaction, row, "REJECTED", null, null, ex.Message, dryRun);
                        result.RejectedRows++;
                        result.Errors.Add("ردیف " + row.SourceRowNumber + ": " + ex.Message);
                        continue;
                    }

                    MarkRow(connection, transaction, row, "IMPORTED", entityType, entityId, null, dryRun,
                        Value(values, target.KeyField));
                    result.ImportedRows++;
                }

                if (!dryRun)
                {
                    using (var update = new SQLiteCommand(SqlBatchUpdate, connection, transaction))
                    {
                        update.Parameters.AddWithValue("@id", batchId);
                        update.Parameters.AddWithValue("@status", "COMMITTED");
                        update.Parameters.AddWithValue("@accepted", result.ImportedRows);
                        update.Parameters.AddWithValue("@rejected", result.RejectedRows);
                        update.Parameters.AddWithValue("@committed", UtcNow());
                        update.ExecuteNonQuery();
                    }
                }
                transaction.Commit();
            }

            result.Status = dryRun ? "VALIDATED" : "COMMITTED";
            return result;
        }

        // ------------------------------------------------------------------
        // Row decoding and validation
        // ------------------------------------------------------------------
        private static Dictionary<string, string> DecodeRow(ImportRowSnapshot row,
            List<ImportColumnMapping> mapping)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            if (row.EncryptedPayload == null || row.EncryptedPayload.Length == 0)
                throw new InvalidOperationException("payload رمزنگاری‌شده این ردیف موجود نیست.");

            string json;
            try
            {
                var plain = ProtectedData.Unprotect(row.EncryptedPayload, Entropy, DataProtectionScope.CurrentUser);
                try { json = Encoding.UTF8.GetString(plain); }
                finally { Array.Clear(plain, 0, plain.Length); }
            }
            catch (CryptographicException)
            {
                throw new InvalidOperationException("ردیف با کلید کاربر فعلی رمزگشایی نشد.");
            }

            var serializer = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 };
            var cells = serializer.Deserialize<string[]>(json);
            if (cells == null) throw new InvalidOperationException("ساختار ردیف منبع نامعتبر است.");

            foreach (var item in mapping)
            {
                if (!item.SourceColumnIndex.HasValue) continue;
                var index = item.SourceColumnIndex.Value;
                if (index < 0 || index >= cells.Length) continue;
                var value = cells[index];
                if (!String.IsNullOrWhiteSpace(value))
                    values[item.TargetField] = value.Trim();
            }
            return values;
        }

        private static string ValidateRow(ImportTarget target, Dictionary<string, string> values)
        {
            foreach (var field in ImportTargetCatalog.RequiredFields(target))
            {
                if (!values.ContainsKey(field))
                    return "فیلد الزامی «" + field + "» خالی است.";
            }
            if (!String.IsNullOrWhiteSpace(target.DateField))
            {
                try { FiscalYearService.ResolveDate(values[target.DateField]); }
                catch (Exception ex) { return ex.Message; }
            }
            if (!String.IsNullOrWhiteSpace(target.PartyField))
            {
                if (!values.ContainsKey(target.PartyField)) return "فیلد الزامی «" + target.PartyField + "» خالی است.";
            }
            return null;
        }

        private static string Value(Dictionary<string, string> values, string field)
        {
            string value;
            return values != null && values.TryGetValue(field, out value) ? value : null;
        }

        private static decimal? DecimalValue(Dictionary<string, string> values, string field)
        {
            var raw = Value(values, field);
            if (String.IsNullOrWhiteSpace(raw)) return null;
            decimal parsed;
            raw = NormalizeDigits(raw);
            if (Decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out parsed))
                return parsed;
            if (Decimal.TryParse(raw, NumberStyles.Number, CultureInfo.GetCultureInfo("fa-IR"), out parsed))
                return parsed;
            return null;
        }

        private static long? IntegerValue(Dictionary<string, string> values, string field)
        {
            var raw = Value(values, field);
            if (String.IsNullOrWhiteSpace(raw)) return null;
            long parsed;
            if (Int64.TryParse(NormalizeDigits(raw), out parsed)) return parsed;
            return null;
        }

        private static string NormalizeDigits(string value)
        {
            if (String.IsNullOrEmpty(value)) return value;
            var chars = value.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                if (chars[i] >= '۰' && chars[i] <= '۹') chars[i] = (char)('0' + (chars[i] - '۰'));
                else if (chars[i] >= '٠' && chars[i] <= '٩') chars[i] = (char)('0' + (chars[i] - '٠'));
                else if (chars[i] == '٫') chars[i] = '.';
                else if (chars[i] == '٬') chars[i] = ',';
            }
            return new string(chars);
        }

        // ------------------------------------------------------------------
        // Import targets
        // ------------------------------------------------------------------
        private static string ImportParty(SQLiteConnection connection, SQLiteTransaction transaction,
            string userId, ImportTarget target, Dictionary<string, string> values, ReferenceResolver resolver)
        {
            var code = Value(values, target.KeyField);
            var kind = Value(values, "نوع شخص") ?? "PERSON";
            var nationalId = Value(values, "کد ملی / شناسه ملی");
            var mobile = Value(values, "موبایل");
            var phone = Value(values, "تلفن");
            var address = Value(values, "نشانی");
            var notes = Value(values, "یادداشت");
            return MasterDataService.CreatePartyWithCode(connection, transaction, code, kind,
                Value(values, "نام نمایشی"), nationalId, null, mobile, phone, address, notes, userId);
        }

        private static string ImportProduct(SQLiteConnection connection, SQLiteTransaction transaction,
            string userId, ImportTarget target, Dictionary<string, string> values, ReferenceResolver resolver)
        {
            return MasterDataService.CreateProductWithCode(connection, transaction,
                Value(values, target.KeyField), Value(values, "نام محصول"),
                Value(values, "کد گروه محصول"), Value(values, "کد واحد"), userId);
        }

        private static string ImportDocument(SQLiteConnection connection, SQLiteTransaction transaction,
            string databasePath, string userId, ImportTarget target,
            Dictionary<string, string> values, ReferenceResolver resolver)
        {
            var documentId = Guid.NewGuid().ToString("N");
            var dateIso = String.IsNullOrWhiteSpace(target.DateField)
                ? DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : FiscalYearService.ResolveDate(values[target.DateField]);
            var fiscalYear = FiscalYearService.Resolve(databasePath, dateIso);
            var partyAccount = String.IsNullOrWhiteSpace(target.PartyField)
                ? null : resolver.PartyAccount(Value(values, target.PartyField));
            var reference = Value(values, target.KeyField);
            var productId = String.IsNullOrWhiteSpace(target.ProductField)
                ? null : resolver.Product(Value(values, target.ProductField));
            var packagingId = resolver.Packaging(Value(values, "کد نوع سبد"));
            var sourceDocumentId = String.IsNullOrWhiteSpace(target.SourceDocumentField)
                ? null : resolver.Document(Value(values, target.SourceDocumentField));

            var quantityGrams = FirstQuantity(values);
            var amount = FirstAmount(values);
            var unitPrice = FirstPrice(values);
            if (!amount.HasValue && unitPrice.HasValue && quantityGrams.HasValue && quantityGrams.Value > 0)
                amount = (long)(unitPrice.Value * (quantityGrams.Value / 1000m));

            using (var command = new SQLiteCommand(SqlDocumentInsert, connection, transaction))
            {
                command.Parameters.AddWithValue("@id", documentId);
                command.Parameters.AddWithValue("@type", target.DocumentType);
                command.Parameters.AddWithValue("@fiscal", fiscalYear);
                command.Parameters.AddWithValue("@date", dateIso);
                command.Parameters.AddWithValue("@party", (object)partyAccount ?? DBNull.Value);
                command.Parameters.AddWithValue("@reference", (object)reference ?? DBNull.Value);
                command.Parameters.AddWithValue("@notes",
                    (object)(target.ProfileCode + " | " + (sourceDocumentId != null ? "منبع: " + sourceDocumentId : "")) ?? DBNull.Value);
                command.Parameters.AddWithValue("@user", userId);
                command.ExecuteNonQuery();
            }

            if (productId != null || quantityGrams.HasValue || amount.HasValue)
            {
                using (var line = new SQLiteCommand(SqlDocumentLineInsert, connection, transaction))
                {
                    line.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                    line.Parameters.AddWithValue("@document", documentId);
                    line.Parameters.AddWithValue("@product", (object)productId ?? DBNull.Value);
                    line.Parameters.AddWithValue("@packaging", (object)packagingId ?? DBNull.Value);
                    line.Parameters.AddWithValue("@quantityG", (object)quantityGrams ?? DBNull.Value);
                    line.Parameters.AddWithValue("@quantityCount", (object)IntegerValue(values, "تعداد") ?? DBNull.Value);
                    line.Parameters.AddWithValue("@unitPrice", (object)unitPrice ?? DBNull.Value);
                    line.Parameters.AddWithValue("@amount", amount ?? 0L);
                    line.Parameters.AddWithValue("@description",
                        (object)(target.ProfileCode + " ردیف " + reference) ?? DBNull.Value);
                    line.ExecuteNonQuery();
                }
            }

            using (var history = new SQLiteCommand(SqlStatusHistoryInsert, connection, transaction))
            {
                history.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                history.Parameters.AddWithValue("@document", documentId);
                history.Parameters.AddWithValue("@user", userId);
                history.Parameters.AddWithValue("@reason", "ورود از فایل اکسل/CSV؛ پیش‌نویس بدون اثر مالی یا انباری");
                history.ExecuteNonQuery();
            }

            if (sourceDocumentId != null)
            {
                using (var relation = new SQLiteCommand(
                    "INSERT INTO document_relations(id,source_document_id,target_document_id,relation_type,notes) "
                    + "VALUES(@id,@source,@target,'SOURCE_OF',@notes);", connection, transaction))
                {
                    relation.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                    relation.Parameters.AddWithValue("@source", sourceDocumentId);
                    relation.Parameters.AddWithValue("@target", documentId);
                    relation.Parameters.AddWithValue("@notes", (object)target.ProfileCode ?? DBNull.Value);
                    relation.ExecuteNonQuery();
                }
            }

            using (var audit = new SQLiteCommand(SqlAuditInsert, connection, transaction))
            {
                audit.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                audit.Parameters.AddWithValue("@actor", userId);
                audit.Parameters.AddWithValue("@action", "IMPORT_DOCUMENT");
                audit.Parameters.AddWithValue("@entityType", "DOCUMENT");
                audit.Parameters.AddWithValue("@entityId", documentId);
                audit.Parameters.AddWithValue("@after", SafeJson("document_type", target.DocumentType,
                    "reference_number", reference ?? ""));
                audit.Parameters.AddWithValue("@machine", Environment.MachineName);
                audit.ExecuteNonQuery();
            }
            return documentId;
        }

        private static long? FirstQuantity(Dictionary<string, string> values)
        {
            foreach (var field in new[] {
                "وزن خالص (kg)", "وزن ناخالص (kg)", "وزن خرید (kg)", "وزن فروش (kg)",
                "وزن ورودی (kg)", "وزن خروجی (kg)" })
            {
                var kg = DecimalValue(values, field);
                if (kg.HasValue)
                {
                    if (kg.Value < 0) throw new ArgumentException("وزن نمی‌تواند منفی باشد.");
                    return checked((long)(kg.Value * 1000m));
                }
            }
            return null;
        }

        private static long? FirstAmount(Dictionary<string, string> values)
        {
            foreach (var field in new[] { "مبلغ اصلی (ریال)", "مبلغ (ریال)", "مبلغ تخصیص (ریال)" })
            {
                var amount = IntegerValue(values, field);
                if (amount.HasValue)
                {
                    if (amount.Value < 0) throw new ArgumentException("مبلغ نمی‌تواند منفی باشد.");
                    return amount.Value;
                }
            }
            return null;
        }

        private static long? FirstPrice(Dictionary<string, string> values)
        {
            foreach (var field in new[] { "فی خرید (ریال/kg)", "فی فروش (ریال/kg)" })
            {
                var price = IntegerValue(values, field);
                if (price.HasValue)
                {
                    if (price.Value < 0) throw new ArgumentException("فی نمی‌تواند منفی باشد.");
                    return price.Value;
                }
            }
            return null;
        }

        private static string SafeJson(string key1, string value1, string key2, string value2)
        {
            return "{\"" + key1 + "\":\"" + Escape(value1) + "\",\"" + key2 + "\":\"" + Escape(value2) + "\"}";
        }

        private static string Escape(string value)
        {
            return (value ?? String.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static string UtcNow()
        {
            return DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        }

        // ------------------------------------------------------------------
        // Infrastructure
        // ------------------------------------------------------------------
        private static void RequireCommitPermission(SQLiteConnection connection,
            SQLiteTransaction transaction, string userId)
        {
            using (var command = new SQLiteCommand(SqlPermissionCheck, connection, transaction))
            {
                command.Parameters.AddWithValue("@user", userId ?? String.Empty);
                if (Convert.ToInt32(command.ExecuteScalar()) != 1)
                    throw new UnauthorizedAccessException(
                        "انتقال اطلاعات به جداول عملیاتی فقط برای کاربر دارای مجوز «ثبت سند» مجاز است.");
            }
        }

        private static object LookupBatchProfile(string databasePath, string batchId)
        {
            using (var connection = Open(databasePath))
            using (var command = new SQLiteCommand(
                "SELECT import_profile FROM import_batches WHERE id=@id;", connection))
            {
                command.Parameters.AddWithValue("@id", batchId);
                return command.ExecuteScalar();
            }
        }

        private static ImportBatchSnapshot LoadBatch(SQLiteConnection connection,
            SQLiteTransaction transaction, string batchId)
        {
            using (var command = new SQLiteCommand(SqlBatchLoad, connection, transaction))
            {
                command.Parameters.AddWithValue("@id", batchId);
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read()) throw new InvalidOperationException("بسته ورود اطلاعات پیدا نشد.");
                    return new ImportBatchSnapshot {
                        Id = reader.GetString(0), SourceFileName = reader.GetString(1),
                        ProfileCode = reader.GetString(2), Status = reader.GetString(3),
                        TotalRows = reader.GetInt32(4), ColumnMappingJson = reader.GetString(7)
                    };
                }
            }
        }

        private static List<ImportRowSnapshot> LoadRows(SQLiteConnection connection,
            SQLiteTransaction transaction, string batchId)
        {
            var rows = new List<ImportRowSnapshot>();
            using (var command = new SQLiteCommand(SqlRows, connection, transaction))
            {
                command.Parameters.AddWithValue("@batch", batchId);
                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                        rows.Add(new ImportRowSnapshot {
                            Id = reader.GetString(0), SourceRowNumber = reader.GetInt32(1),
                            RowStatus = reader.GetString(3), SafeJson = reader.GetString(6),
                            SourceRowSha256 = reader.GetString(8),
                            EncryptedPayload = reader.IsDBNull(9) ? null : (byte[])reader.GetValue(9)
                        });
            }
            return rows;
        }

        private static void MarkRow(SQLiteConnection connection, SQLiteTransaction transaction,
            ImportRowSnapshot row, string status, string entityType, string entityId,
            string error, bool dryRun, string externalKey = null)
        {
            if (dryRun) return;
            using (var command = new SQLiteCommand(SqlRowUpdate, connection, transaction))
            {
                command.Parameters.AddWithValue("@id", row.Id);
                command.Parameters.AddWithValue("@key", (object)externalKey ?? DBNull.Value);
                command.Parameters.AddWithValue("@status", status);
                command.Parameters.AddWithValue("@entity", (object)entityType ?? DBNull.Value);
                command.Parameters.AddWithValue("@target", (object)entityId ?? DBNull.Value);
                command.Parameters.AddWithValue("@errors", (object)error ?? DBNull.Value);
                command.ExecuteNonQuery();
            }
        }

        private static bool RowAlreadyImported(SQLiteConnection connection, SQLiteTransaction transaction,
            string hash)
        {
            using (var command = new SQLiteCommand(SqlDuplicateCheck, connection, transaction))
            {
                command.Parameters.AddWithValue("@hash", hash);
                return Convert.ToInt32(command.ExecuteScalar()) > 0;
            }
        }

        private static List<ImportColumnMapping> ReadMapping(string json)
        {
            var mapping = new List<ImportColumnMapping>();
            if (String.IsNullOrWhiteSpace(json)) return mapping;
            var serializer = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 };
            var payload = serializer.Deserialize<Dictionary<string, object>>(json);
            if (payload == null || !payload.ContainsKey("fields")) return mapping;
            foreach (var entry in (ArrayList)payload["fields"])
            {
                var item = entry as Dictionary<string, object>;
                if (item == null) continue;
                int? index = null;
                if (item.ContainsKey("sourceColumnIndex") && item["sourceColumnIndex"] != null)
                    index = Convert.ToInt32(item["sourceColumnIndex"]);
                mapping.Add(new ImportColumnMapping {
                    TargetField = Convert.ToString(item["targetField"]),
                    Required = item.ContainsKey("required") && Convert.ToBoolean(item["required"]),
                    SourceColumnIndex = index,
                    SourceHeader = item.ContainsKey("sourceHeader") ? Convert.ToString(item["sourceHeader"]) : null
                });
            }
            return mapping;
        }

        private static SQLiteConnection Open(string databasePath)
        {
            var connection = new SQLiteConnection(
                "Data Source=" + databasePath + ";Version=3;Pooling=True;");
            connection.Open();
            using (var command = new SQLiteCommand(
                "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000; PRAGMA synchronous=FULL;", connection))
                command.ExecuteNonQuery();
            return connection;
        }

        private sealed class ReferenceResolver
        {
            private readonly SQLiteConnection _connection;
            private readonly SQLiteTransaction _transaction;
            private readonly Dictionary<string, string> _partyCache = new Dictionary<string, string>();
            private readonly Dictionary<string, string> _productCache = new Dictionary<string, string>();
            private readonly Dictionary<string, string> _packagingCache = new Dictionary<string, string>();
            private readonly Dictionary<string, string> _documentCache = new Dictionary<string, string>();

            public ReferenceResolver(SQLiteConnection connection, SQLiteTransaction transaction)
            {
                _connection = connection;
                _transaction = transaction;
            }

            public string PartyAccount(string code)
            {
                if (String.IsNullOrWhiteSpace(code)) return null;
                var key = code.Trim().ToUpperInvariant();
                string cached;
                if (_partyCache.TryGetValue(key, out cached)) return cached;
                using (var command = new SQLiteCommand(
                    "SELECT pa.id FROM party_accounts pa "
                    + "JOIN ledger_accounts la ON la.id=pa.ledger_account_id "
                    + "WHERE la.account_code=@code AND pa.is_active=1;", _connection, _transaction))
                {
                    command.Parameters.AddWithValue("@code", key);
                    var id = Convert.ToString(command.ExecuteScalar());
                    if (id == null)
                        throw new ArgumentException("کد طرف حساب «" + code + "» در فهرست وجود ندارد.");
                    _partyCache[key] = id;
                    return id;
                }
            }

            public string Product(string code)
            {
                if (String.IsNullOrWhiteSpace(code)) return null;
                var key = code.Trim().ToUpperInvariant();
                string cached;
                if (_productCache.TryGetValue(key, out cached)) return cached;
                using (var command = new SQLiteCommand(
                    "SELECT id FROM products WHERE product_code=@code AND is_active=1;",
                    _connection, _transaction))
                {
                    command.Parameters.AddWithValue("@code", key);
                    var id = Convert.ToString(command.ExecuteScalar());
                    if (id == null)
                        throw new ArgumentException("کد محصول «" + code + "» در فهرست وجود ندارد.");
                    _productCache[key] = id;
                    return id;
                }
            }

            public string Packaging(string code)
            {
                if (String.IsNullOrWhiteSpace(code)) return null;
                var key = code.Trim().ToUpperInvariant();
                string cached;
                if (_packagingCache.TryGetValue(key, out cached)) return cached;
                using (var command = new SQLiteCommand(
                    "SELECT id FROM packaging_types WHERE packaging_code=@code AND is_active=1;",
                    _connection, _transaction))
                {
                    command.Parameters.AddWithValue("@code", key);
                    var id = Convert.ToString(command.ExecuteScalar());
                    _packagingCache[key] = id;
                    return id;
                }
            }

            public string Document(string documentNumber)
            {
                if (String.IsNullOrWhiteSpace(documentNumber)) return null;
                var key = documentNumber.Trim();
                string cached;
                if (_documentCache.TryGetValue(key, out cached)) return cached;
                using (var command = new SQLiteCommand(
                    "SELECT id FROM documents WHERE document_number=@number;", _connection, _transaction))
                {
                    command.Parameters.AddWithValue("@number", key);
                    var id = Convert.ToString(command.ExecuteScalar());
                    if (id == null)
                        throw new ArgumentException("سند مرجع «" + documentNumber + "» پیدا نشد.");
                    _documentCache[key] = id;
                    return id;
                }
            }
        }

        private sealed class ImportBatchSnapshot
        {
            public string Id { get; set; }
            public string SourceFileName { get; set; }
            public string ProfileCode { get; set; }
            public string Status { get; set; }
            public int TotalRows { get; set; }
            public string ColumnMappingJson { get; set; }
        }

        private sealed class ImportRowSnapshot
        {
            public string Id { get; set; }
            public int SourceRowNumber { get; set; }
            public string RowStatus { get; set; }
            public string SafeJson { get; set; }
            public string SourceRowSha256 { get; set; }
            public byte[] EncryptedPayload { get; set; }
        }
    }
}
