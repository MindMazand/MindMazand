using System;
using System.Data.SQLite;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using Mazandasht.Desktop.Security;

namespace Mazandasht.Desktop.Import
{
    public static class ImportStagingService
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Mazandasht.ImportStage.v1");

        public static string SavePreview(string databasePath, WorkbookPreview preview, string importProfile,
            string columnMappingJson, int headerRowNumber)
        {
            if (preview == null) throw new ArgumentNullException("preview");
            if (String.IsNullOrWhiteSpace(databasePath) || !File.Exists(databasePath))
                throw new FileNotFoundException("پایگاه داده عملیاتی پیدا نشد.", databasePath);
            if (AppSession.CurrentUser == null || String.IsNullOrWhiteSpace(AppSession.CurrentUser.UserId))
                throw new UnauthorizedAccessException("برای ایجاد بسته ورود اطلاعات باید کاربر وارد شده باشد.");
            if (!String.Equals(AppSession.CurrentUser.RoleCode, "ADMIN", StringComparison.OrdinalIgnoreCase) &&
                !String.Equals(AppSession.CurrentUser.RoleCode, "MANAGER", StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("ورود فایل‌های منبع فقط برای مدیر سیستم یا مدیر مجموعه مجاز است.");
            if (String.IsNullOrWhiteSpace(preview.Sha256) || preview.Sha256.Length != 64)
                throw new InvalidOperationException("اثر انگشت فایل معتبر نیست.");
            if (ImportProfileCatalog.Find(importProfile) == null)
                throw new InvalidOperationException("پروفایل ورود اطلاعات در فهرست ماژول‌های پشتیبانی‌شده وجود ندارد.");
            if (preview.TotalRows < 2 || preview.TotalRows > ImportWorkbookReader.MaximumRowsPerSheet)
                throw new InvalidOperationException("شیت باید یک ردیف عنوان و دست‌کم یک ردیف داده داشته باشد.");
            if (String.IsNullOrWhiteSpace(columnMappingJson) || headerRowNumber < 1)
                throw new InvalidOperationException("نگاشت ستون‌ها و شماره ردیف عنوان الزامی است.");
            if (preview.Rows == null || preview.Rows.Count == 0 || preview.Rows[0].RowNumber != headerRowNumber)
                throw new InvalidOperationException("ردیف عنوان باید اولین ردیف غیرخالی شیت باشد؛ فایل را اصلاح و دوباره پیش‌نمایش کنید.");

            var batchId = Guid.NewGuid().ToString("N");
            var serializer = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 };
            if (!String.Equals(ComputeSha256(preview.FilePath), preview.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("فایل منبع پس از پیش‌نمایش تغییر کرده است؛ دوباره آن را بارگذاری کنید.");

            using (var connection = Open(databasePath))
            using (var transaction = connection.BeginTransaction())
            {
                using (var catalog = new SQLiteCommand(@"
                    INSERT OR IGNORE INTO source_file_catalog
                    (source_sha256,source_file_name,source_size_bytes,source_kind,sheet_count,imported_by,notes)
                    VALUES(@hash,@name,@size,@kind,@sheets,@user,@notes);", connection, transaction))
                {
                    catalog.Parameters.AddWithValue("@hash", preview.Sha256);
                    catalog.Parameters.AddWithValue("@name", preview.FileName);
                    catalog.Parameters.AddWithValue("@size", preview.SizeBytes);
                    catalog.Parameters.AddWithValue("@kind", InferSourceKind(importProfile));
                    catalog.Parameters.AddWithValue("@sheets", preview.Sheets.Count);
                    catalog.Parameters.AddWithValue("@user", AppSession.CurrentUser.UserId);
                    catalog.Parameters.AddWithValue("@notes", "ثبت در صف پیش‌نمایش؛ هنوز به جداول عملیاتی منتقل نشده است.");
                    catalog.ExecuteNonQuery();
                }

                using (var batch = new SQLiteCommand(@"
                    INSERT INTO import_batches
                    (id,source_file_name,source_file_sha256,source_sheet_name,import_profile,status,total_rows,accepted_rows,rejected_rows,created_by,column_mapping_json,header_row_number)
                    VALUES(@id,@file,@hash,@sheet,@profile,'PREVIEW',@total,0,0,@user,@mapping,@header);", connection, transaction))
                {
                    batch.Parameters.AddWithValue("@id", batchId);
                    batch.Parameters.AddWithValue("@file", preview.FileName);
                    batch.Parameters.AddWithValue("@hash", preview.Sha256);
                    batch.Parameters.AddWithValue("@sheet", preview.SelectedSheet);
                    batch.Parameters.AddWithValue("@profile", String.IsNullOrWhiteSpace(importProfile) ? "GENERIC_REVIEW_ONLY" : importProfile);
                    batch.Parameters.AddWithValue("@total", preview.TotalRows - 1);
                    batch.Parameters.AddWithValue("@user", AppSession.CurrentUser.UserId);
                    batch.Parameters.AddWithValue("@mapping", columnMappingJson);
                    batch.Parameters.AddWithValue("@header", headerRowNumber);
                    batch.ExecuteNonQuery();
                }

                using (var rowCommand = new SQLiteCommand(@"
                    INSERT INTO import_rows
                    (id,import_batch_id,source_row_number,external_key,row_status,target_entity,target_id,
                     raw_row_json,validation_errors_json,source_row_sha256,raw_sensitive_enc)
                    VALUES(@id,@batch,@row,NULL,'PENDING',NULL,NULL,@safe,NULL,@rowhash,@encrypted);", connection, transaction))
                {
                    rowCommand.Parameters.Add("@id", System.Data.DbType.String);
                    rowCommand.Parameters.Add("@batch", System.Data.DbType.String).Value = batchId;
                    rowCommand.Parameters.Add("@row", System.Data.DbType.Int32);
                    rowCommand.Parameters.Add("@safe", System.Data.DbType.String);
                    rowCommand.Parameters.Add("@rowhash", System.Data.DbType.String);
                    rowCommand.Parameters.Add("@encrypted", System.Data.DbType.Binary);

                    foreach (var row in ImportWorkbookReader.ReadAllRows(preview.FilePath, preview.SelectedSheet, headerRowNumber))
                    {
                        var payload = serializer.Serialize(row.Cells);
                        var payloadBytes = Encoding.UTF8.GetBytes(payload);
                        byte[] encrypted;
                        try
                        {
                            encrypted = ProtectedData.Protect(payloadBytes, Entropy, DataProtectionScope.CurrentUser);
                        }
                        finally
                        {
                            Array.Clear(payloadBytes, 0, payloadBytes.Length);
                        }

                        rowCommand.Parameters["@id"].Value = Guid.NewGuid().ToString("N");
                        rowCommand.Parameters["@row"].Value = row.RowNumber;
                        rowCommand.Parameters["@safe"].Value = serializer.Serialize(new
                        {
                            sourceSheet = preview.SelectedSheet,
                            sourceRowNumber = row.RowNumber,
                            cellCount = row.Cells.Length,
                            sensitivePayloadEncrypted = true
                        });
                        rowCommand.Parameters["@rowhash"].Value = HashRow(row.Cells);
                        rowCommand.Parameters["@encrypted"].Value = encrypted;
                        rowCommand.ExecuteNonQuery();
                        Array.Clear(encrypted, 0, encrypted.Length);
                    }
                }

                if (!String.Equals(ComputeSha256(preview.FilePath), preview.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("فایل منبع حین ذخیره تغییر کرده است؛ عملیات لغو شد.");
                transaction.Commit();
            }
            return batchId;
        }

        private static string InferSourceKind(string profile)
        {
            var value = (profile ?? String.Empty).ToUpperInvariant();
            if (value.Contains("BANK")) return "BANK_STATEMENT";
            if (value.Contains("PARTNER")) return "PARTNER_STATEMENT";
            if (value.Contains("MASTER")) return "MASTER";
            if (value.Contains("WEIGH") || value.Contains("BASKET") || value.Contains("SALES") ||
                value.Contains("PURCHASE") || value.Contains("SORTING") || value.Contains("CHEQUE") ||
                value.Contains("SETTLEMENT")) return "OPERATIONAL";
            return "OTHER";
        }

        private static string ComputeSha256(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(stream);
                return BitConverter.ToString(bytes).Replace("-", String.Empty).ToLowerInvariant();
            }
        }

        private static string HashRow(string[] cells)
        {
            var json = new JavaScriptSerializer().Serialize(cells);
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(json));
                return BitConverter.ToString(bytes).Replace("-", String.Empty).ToLowerInvariant();
            }
        }

        private static SQLiteConnection Open(string path)
        {
            var connection = new SQLiteConnection("Data Source=" + path + ";Version=3;Pooling=True;");
            connection.Open();
            using (var command = new SQLiteCommand("PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000; PRAGMA synchronous=FULL;", connection))
                command.ExecuteNonQuery();
            return connection;
        }
    }
}
