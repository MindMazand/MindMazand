using System;
using System.Data;
using System.Data.SQLite;
using Mazandasht.Desktop.Services;

namespace Mazandasht.Desktop.Services
{
    public static class ApprovalService
    {
        public static void ApproveDocument(string path, string documentId, string managerUserId, string reason)
        {
            if (String.IsNullOrWhiteSpace(documentId) || String.IsNullOrWhiteSpace(managerUserId))
                throw new ArgumentException("شناسه سند و مدیر تأییدکننده الزامی است.");
            using (var c = new SQLiteConnection("Data Source=" + path + ";Version=3;Pooling=True;"))
            {
                c.Open();
                using (var pragma = new SQLiteCommand("PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;", c)) pragma.ExecuteNonQuery();
                using (var tx = c.BeginTransaction(IsolationLevel.Serializable))
                {
                    string roleCode;
                    using (var roleCmd = new SQLiteCommand(@"SELECT r.role_code FROM users u JOIN roles r ON r.id=u.role_id
                        WHERE u.id=@user AND u.is_active=1;", c, tx))
                    {
                        roleCmd.Parameters.AddWithValue("@user", managerUserId);
                        roleCode = Convert.ToString(roleCmd.ExecuteScalar());
                    }
                    if (roleCode != "ADMIN" && roleCode != "MANAGER")
                        throw new UnauthorizedAccessException("فقط مدیر سیستم یا مدیر مجموعه مجاز به تأیید سند است.");

                    string oldStatus;
                    using (var cmd = new SQLiteCommand("SELECT status FROM documents WHERE id=@id;", c, tx))
                    {
                        cmd.Parameters.AddWithValue("@id", documentId);
                        oldStatus = Convert.ToString(cmd.ExecuteScalar());
                    }
                    if (oldStatus != "REVIEW") throw new InvalidOperationException("فقط سند در وضعیت بررسی قابل تأیید است.");
                    using (var cmd = new SQLiteCommand(@"UPDATE documents SET status='APPROVED',approved_by=@user,
                        approved_at_utc=strftime('%Y-%m-%dT%H:%M:%fZ','now'),updated_at_utc=strftime('%Y-%m-%dT%H:%M:%fZ','now')
                        WHERE id=@id AND status='REVIEW';", c, tx))
                    {
                        cmd.Parameters.AddWithValue("@user", managerUserId);
                        cmd.Parameters.AddWithValue("@id", documentId);
                        if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException("سند هم‌زمان تغییر کرده یا دیگر قابل تأیید نیست.");
                    }
                    // Post the document's journal entry now that the document is
                    // APPROVED. The database trigger refuses an unbalanced entry, an
                    // entry with fewer than two lines, or an entry for a document that
                    // is not approved (G9).
                    using (var cmd = new SQLiteCommand(@"UPDATE journal_entries SET is_posted=1,
                        posted_by=@user, posted_at_utc=strftime('%Y-%m-%dT%H:%M:%fZ','now')
                        WHERE document_id=@doc AND is_posted=0;", c, tx))
                    {
                        cmd.Parameters.AddWithValue("@user", managerUserId);
                        cmd.Parameters.AddWithValue("@doc", documentId);
                        cmd.ExecuteNonQuery();
                    }
                    using (var cmd = new SQLiteCommand(@"UPDATE stock_movements SET is_posted=1
                        WHERE source_document_id=@doc AND is_posted=0;", c, tx))
                    {
                        cmd.Parameters.AddWithValue("@doc", documentId);
                        cmd.ExecuteNonQuery();
                    }
                    using (var cmd = new SQLiteCommand(@"INSERT INTO document_status_history(id,document_id,old_status,new_status,changed_by,reason)
                        VALUES(@id,@doc,'REVIEW','APPROVED',@user,@reason);", c, tx))
                    {
                        cmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                        cmd.Parameters.AddWithValue("@doc", documentId);
                        cmd.Parameters.AddWithValue("@user", managerUserId);
                        cmd.Parameters.AddWithValue("@reason", (object)reason ?? DBNull.Value);
                        cmd.ExecuteNonQuery();
                    }
                    using (var audit = new SQLiteCommand(@"INSERT INTO audit_log(id,actor_user_id,action_code,entity_type,entity_id,after_json,machine_name)
                        VALUES(@id,@user,'APPROVE_DOCUMENT','DOCUMENT',@doc,@after,@machine);", c, tx))
                    {
                        audit.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                        audit.Parameters.AddWithValue("@user", managerUserId);
                        audit.Parameters.AddWithValue("@doc", documentId);
                        audit.Parameters.AddWithValue("@after", "{\"status\":\"APPROVED\"}");
                        audit.Parameters.AddWithValue("@machine", Environment.MachineName);
                        audit.ExecuteNonQuery();
                    }
                    tx.Commit();
                }
            }
        }
    }
}
