using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Text;
using System.Web.Script.Serialization;

namespace Mazandasht.Desktop.Import
{
    public sealed class ImportBatchRow
    {
        public string Id { get; set; }
        public string SourceFileName { get; set; }
        public string SourceSheetName { get; set; }
        public string ImportProfile { get; set; }
        public string ProfileDisplay { get; set; }
        public string Status { get; set; }
        public int TotalRows { get; set; }
        public int AcceptedRows { get; set; }
        public int RejectedRows { get; set; }
        public string CreatedAtUtc { get; set; }
        public string CommittedAtUtc { get; set; }
        public string TargetKind { get; set; }
    }

    public sealed class ImportBatchSummary
    {
        public ImportBatchRow Batch { get; set; }
        public IList<ImportRowView> Rows { get; set; }
        public int MappedFieldCount { get; set; }
        public int MissingRequiredFieldCount { get; set; }
    }

    /// <summary>
    /// A row shown in the queue. Only safe metadata is exposed here: the source
    /// payload itself stays DPAPI-encrypted until the commit engine decrypts it.
    /// </summary>
    public sealed class ImportRowView
    {
        public string RowNumber { get; set; }
        public string Status { get; set; }
        public string TargetEntity { get; set; }
        public string Error { get; set; }
    }

    public static class ImportQueueService
    {
        private const string SqlBatches =
            @"SELECT id,source_file_name,source_sheet_name,import_profile,status,total_rows,
                     accepted_rows,rejected_rows,created_at_utc,committed_at_utc
              FROM import_batches ORDER BY created_at_utc DESC LIMIT 300;";
        private const string SqlBatch =
            @"SELECT id,source_file_name,source_sheet_name,import_profile,status,total_rows,
                     accepted_rows,rejected_rows,created_at_utc,committed_at_utc,column_mapping_json
              FROM import_batches WHERE id=@id;";
        private const string SqlRows =
            @"SELECT source_row_number,row_status,target_entity,validation_errors_json
              FROM import_rows WHERE import_batch_id=@batch ORDER BY source_row_number;";
        private const string SqlCancel =
            @"UPDATE import_batches SET status='CANCELLED' WHERE id=@id AND status='PREVIEW';";

        public static IList<ImportBatchRow> ListBatches(string databasePath)
        {
            var rows = new List<ImportBatchRow>();
            using (var connection = Open(databasePath))
            using (var command = new SQLiteCommand(SqlBatches, connection))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                    rows.Add(NewBatch(reader));
            return rows;
        }

        public static ImportBatchSummary GetSummary(string databasePath, string batchId)
        {
            ImportBatchRow batch = null;
            var mappingJson = String.Empty;
            using (var connection = Open(databasePath))
            using (var command = new SQLiteCommand(SqlBatch, connection))
            {
                command.Parameters.AddWithValue("@id", batchId);
                using (var reader = command.ExecuteReader())
                    if (reader.Read())
                    {
                        batch = NewBatch(reader);
                        mappingJson = reader.GetString(10);
                    }
            }
            if (batch == null) throw new InvalidOperationException("بسته ورود اطلاعات پیدا نشد.");

            var rows = new List<ImportRowView>();
            var mapped = 0;
            var missingRequired = 0;
            var target = ImportTargetCatalog.Find(batch.ImportProfile);
            var required = target == null ? new List<string>() : ImportTargetCatalog.RequiredFields(target);

            using (var connection = Open(databasePath))
            {
                using (var command = new SQLiteCommand(SqlRows, connection))
                {
                    command.Parameters.AddWithValue("@batch", batchId);
                    using (var reader = command.ExecuteReader())
                        while (reader.Read())
                            rows.Add(new ImportRowView {
                                RowNumber = reader.GetInt32(0).ToString(),
                                Status = StatusLabel(reader.GetString(1)),
                                TargetEntity = reader.IsDBNull(2) ? "—" : reader.GetString(2),
                                Error = reader.IsDBNull(3) ? null : reader.GetString(3)
                            });
                }

                var serializer = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 };
                var payload = serializer.Deserialize<Dictionary<string, object>>(mappingJson ?? "{}");
                var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (payload != null && payload.ContainsKey("fields"))
                {
                    foreach (var entry in (System.Collections.ArrayList)payload["fields"])
                    {
                        var item = entry as Dictionary<string, object>;
                        if (item == null) continue;
                        var name = Convert.ToString(item["targetField"]);
                        var hasIndex = item.ContainsKey("sourceColumnIndex") && item["sourceColumnIndex"] != null;
                        if (hasIndex) { fields.Add(name); mapped++; }
                        else if (required.Contains(name)) missingRequired++;
                    }
                }
            }

            return new ImportBatchSummary {
                Batch = batch, Rows = rows,
                MappedFieldCount = mapped, MissingRequiredFieldCount = missingRequired
            };
        }

        /// <summary>
        /// Cancels a batch that has not been committed yet.
        /// </summary>
        public static bool CancelBatch(string databasePath, string batchId)
        {
            using (var connection = Open(databasePath))
            using (var transaction = connection.BeginTransaction())
            using (var command = new SQLiteCommand(SqlCancel, connection, transaction))
            {
                command.Parameters.AddWithValue("@id", batchId);
                var affected = command.ExecuteNonQuery();
                transaction.Commit();
                return affected == 1;
            }
        }

        private static ImportBatchRow NewBatch(SQLiteDataReader reader)
        {
            var profile = reader.GetString(3);
            var target = ImportTargetCatalog.Find(profile);
            return new ImportBatchRow {
                Id = reader.GetString(0), SourceFileName = reader.GetString(1),
                SourceSheetName = reader.GetString(2), ImportProfile = profile,
                ProfileDisplay = target == null ? profile : ImportProfileCatalog.Find(profile).DisplayName,
                Status = StatusLabel(reader.GetString(4)), TotalRows = reader.GetInt32(5),
                AcceptedRows = reader.GetInt32(6), RejectedRows = reader.GetInt32(7),
                CreatedAtUtc = reader.GetString(8),
                CommittedAtUtc = reader.IsDBNull(9) ? null : reader.GetString(9),
                TargetKind = target == null ? "—" : KindLabel(target.Kind)
            };
        }

        private static string KindLabel(ImportTargetKind kind)
        {
            switch (kind)
            {
                case ImportTargetKind.CatalogParty: return "اطلاعات پایه | اشخاص";
                case ImportTargetKind.CatalogProduct: return "اطلاعات پایه | محصولات";
                default: return "سند پیش‌نویس";
            }
        }

        private static string StatusLabel(string value)
        {
            switch (value)
            {
                case "PREVIEW": return "پیش‌نمایش";
                case "VALIDATED": return "اعتبارسنجی‌شده";
                case "COMMITTED": return "منتقل‌شده";
                case "CANCELLED": return "لغوشده";
                case "FAILED": return "ناموفق";
                default: return value;
            }
        }

        private static SQLiteConnection Open(string databasePath)
        {
            var connection = new SQLiteConnection(
                "Data Source=" + databasePath + ";Version=3;Pooling=True;");
            connection.Open();
            using (var command = new SQLiteCommand("PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;", connection))
                command.ExecuteNonQuery();
            return connection;
        }
    }
}
