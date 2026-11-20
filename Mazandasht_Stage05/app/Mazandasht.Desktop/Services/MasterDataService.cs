using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.Security.Cryptography;
using System.IO;
using System.Linq;
using System.Text;

namespace Mazandasht.Desktop.Services
{
    public sealed class LookupItem
    {
        public string Id { get; set; }
        public string Code { get; set; }
        public string Name { get; set; }
        public string Display { get { return String.IsNullOrEmpty(Code) ? Name : Code + " · " + Name; } }
        public override string ToString() { return Display; }
    }

    public static class MasterDataService
    {
        private static SQLiteConnection Open(string path)
        {
            var c = new SQLiteConnection("Data Source=" + path + ";Version=3;Pooling=True;");
            c.Open();
            using (var p = new SQLiteCommand("PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;", c)) p.ExecuteNonQuery();
            return c;
        }

        /// <summary>
        /// Encrypts a sensitive catalog value (bank account number, IBAN) with
        /// DPAPI for the current Windows user. Exposed so BaseDataService can
        /// protect bank account payloads with the same key as party data.
        /// </summary>
        public static byte[] ProtectText(string value)
        {
            return Protect(value);
        }

        private static byte[] Protect(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return null;
            return ProtectedData.Protect(Encoding.UTF8.GetBytes(value.Trim()), null, DataProtectionScope.CurrentUser);
        }

        private static byte[] GetLookupKey()
        {
            var folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mazandasht", "Data");
            Directory.CreateDirectory(folder);
            var keyPath = System.IO.Path.Combine(folder, "lookup-hmac.key.dpapi");
            if (File.Exists(keyPath))
                return ProtectedData.Unprotect(File.ReadAllBytes(keyPath), null, DataProtectionScope.CurrentUser);
            var key = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(key);
            var protectedKey = ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser);
            using (var fs = new FileStream(keyPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                fs.Write(protectedKey, 0, protectedKey.Length);
            return key;
        }

        private static string HashLookup(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return null;
            using (var hmac = new HMACSHA256(GetLookupKey()))
                return BitConverter.ToString(hmac.ComputeHash(Encoding.UTF8.GetBytes(value.Trim()))).Replace("-", "").ToLowerInvariant();
        }

        public static List<LookupItem> Parties(string path)
        {
            var result = new List<LookupItem>();
            using (var c = Open(path))
            using (var cmd = new SQLiteCommand(@"SELECT pa.id, la.account_code, p.display_name
                FROM party_accounts pa JOIN parties p ON p.id=pa.party_id
                JOIN ledger_accounts la ON la.id=pa.ledger_account_id
                WHERE pa.is_active=1 AND p.is_active=1 AND la.is_active=1
                ORDER BY p.display_name;", c))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) result.Add(new LookupItem { Id=r.GetString(0), Code=r.GetString(1), Name=r.GetString(2) });
            return result;
        }

        public static List<LookupItem> Products(string path)
        {
            var result = new List<LookupItem>();
            using (var c = Open(path))
            using (var cmd = new SQLiteCommand(@"SELECT id, product_code, display_name FROM products WHERE is_active=1 ORDER BY display_name;", c))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) result.Add(new LookupItem { Id=r.GetString(0), Code=r.GetString(1), Name=r.GetString(2) });
            return result;
        }

        public static List<LookupItem> Packaging(string path)
        {
            var result = new List<LookupItem>();
            using (var c = Open(path))
            using (var cmd = new SQLiteCommand(@"SELECT id, packaging_code, display_name FROM packaging_types WHERE is_active=1 ORDER BY display_name;", c))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) result.Add(new LookupItem { Id=r.GetString(0), Code=r.GetString(1), Name=r.GetString(2) });
            return result;
        }

        public static string CreateQuickParty(string path, string name, string nationalId, string phone, string address, string actorUserId)
        {
            if (String.IsNullOrWhiteSpace(name)) throw new ArgumentException("نام طرف حساب الزامی است.");
            if (!String.IsNullOrWhiteSpace(nationalId) && !System.Text.RegularExpressions.Regex.IsMatch(nationalId.Trim(), @"^\d{10}$"))
                throw new ArgumentException("کد ملی باید دقیقاً ۱۰ رقم باشد.");
            var id = Guid.NewGuid().ToString("N");
            var ledgerId = Guid.NewGuid().ToString("N");
            var partyAccountId = Guid.NewGuid().ToString("N");
            using (var c = Open(path))
            using (var tx = c.BeginTransaction(IsolationLevel.Serializable))
            {
                var next = Convert.ToInt32(new SQLiteCommand("SELECT COALESCE(MAX(CAST(SUBSTR(account_code,4) AS INTEGER)),0)+1 FROM ledger_accounts WHERE account_code LIKE 'PTY%';", c, tx).ExecuteScalar());
                var code = "PTY" + next.ToString("000000");
                using (var cmd = new SQLiteCommand(@"INSERT INTO parties(id,party_kind,display_name,national_id_enc,national_id_lookup_hmac,phone_enc,address_enc)
                    VALUES(@id,'PERSON',@name,@nid,@hash,@phone,@address);", c, tx))
                {
                    cmd.Parameters.AddWithValue("@id", id);
                    cmd.Parameters.AddWithValue("@name", name.Trim());
                    cmd.Parameters.AddWithValue("@nid", (object)Protect(nationalId) ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@hash", (object)HashLookup(nationalId) ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@phone", (object)Protect(phone) ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@address", (object)Protect(address) ?? DBNull.Value);
                    cmd.ExecuteNonQuery();
                }
                using (var cmd = new SQLiteCommand(@"INSERT INTO ledger_accounts(id,account_code,display_name,account_class,normal_side,is_system_account)
                    VALUES(@id,@code,@name,'CONTROL','DEBIT',0);", c, tx))
                {
                    cmd.Parameters.AddWithValue("@id", ledgerId);
                    cmd.Parameters.AddWithValue("@code", code);
                    cmd.Parameters.AddWithValue("@name", name.Trim());
                    cmd.ExecuteNonQuery();
                }
                using (var audit = new SQLiteCommand(@"INSERT INTO audit_log(id,actor_user_id,action_code,entity_type,entity_id,after_json,machine_name)
                    VALUES(@id,@actor,'CREATE_QUICK','PARTY',@entity,@after,@machine);", c, tx))
                {
                    audit.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                    audit.Parameters.AddWithValue("@actor", (object)actorUserId ?? DBNull.Value);
                    audit.Parameters.AddWithValue("@entity", id);
                    audit.Parameters.AddWithValue("@after", "{\"display_name\":\"" + name.Trim().Replace("\\","\\\\").Replace("\"","\\\"") + "\"}");
                    audit.Parameters.AddWithValue("@machine", Environment.MachineName);
                    audit.ExecuteNonQuery();
                }
                using (var cmd = new SQLiteCommand(@"INSERT INTO party_accounts(id,party_id,ledger_account_id,is_active) VALUES(@id,@party,@ledger,1);", c, tx))
                {
                    cmd.Parameters.AddWithValue("@id", partyAccountId);
                    cmd.Parameters.AddWithValue("@party", id);
                    cmd.Parameters.AddWithValue("@ledger", ledgerId);
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
            return partyAccountId;
        }

        public static string CreateQuickProduct(string path, string name, string groupId, string unitId, string actorUserId)
        {
            if (String.IsNullOrWhiteSpace(name)) throw new ArgumentException("نام محصول الزامی است.");
            var id = Guid.NewGuid().ToString("N");
            using (var c = Open(path))
            using (var tx = c.BeginTransaction(IsolationLevel.Serializable))
            {
                using (var duplicate = new SQLiteCommand(@"SELECT COUNT(*) FROM products
                    WHERE is_active=1 AND display_name=@name AND product_group_id IS @group;", c, tx))
                {
                    duplicate.Parameters.AddWithValue("@name", name.Trim());
                    duplicate.Parameters.AddWithValue("@group", (object)groupId ?? DBNull.Value);
                    if (Convert.ToInt32(duplicate.ExecuteScalar()) > 0)
                        throw new InvalidOperationException("محصول فعال با همین نام در این گروه از قبل وجود دارد.");
                }
                var next = Convert.ToInt32(new SQLiteCommand("SELECT COALESCE(MAX(CAST(SUBSTR(product_code,4) AS INTEGER)),0)+1 FROM products WHERE product_code LIKE 'PRD%';", c, tx).ExecuteScalar());
                using (var cmd = new SQLiteCommand(@"INSERT INTO products(id,product_code,product_group_id,display_name,product_role,unit_id)
                    VALUES(@id,@code,@group,@name,'BOTH',@unit);", c, tx))
                {
                    cmd.Parameters.AddWithValue("@id", id);
                    cmd.Parameters.AddWithValue("@code", "PRD" + next.ToString("000000"));
                    cmd.Parameters.AddWithValue("@group", (object)groupId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@name", name.Trim());
                    cmd.Parameters.AddWithValue("@unit", (object)unitId ?? DBNull.Value);
                    cmd.ExecuteNonQuery();
                }
                using (var audit = new SQLiteCommand(@"INSERT INTO audit_log(id,actor_user_id,action_code,entity_type,entity_id,after_json,machine_name)
                    VALUES(@id,@actor,'CREATE_QUICK','PRODUCT',@entity,@after,@machine);", c, tx))
                {
                    audit.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                    audit.Parameters.AddWithValue("@actor", (object)actorUserId ?? DBNull.Value);
                    audit.Parameters.AddWithValue("@entity", id);
                    audit.Parameters.AddWithValue("@after", "{\"display_name\":\"" + name.Trim().Replace("\\","\\\\").Replace("\"","\\\"") + "\"}");
                    audit.Parameters.AddWithValue("@machine", Environment.MachineName);
                    audit.ExecuteNonQuery();
                }
                tx.Commit();
            }
            return id;
        }

        public static string CreateQuickPackaging(string path, string name, string kind, int tareWeightGrams, string actorUserId)
        {
            if (String.IsNullOrWhiteSpace(name)) throw new ArgumentException("نام بسته‌بندی الزامی است.");
            if (tareWeightGrams < 0) throw new ArgumentOutOfRangeException("tareWeightGrams");
            if (!new[] { "BASKET","BOX","CARTON","BAG","BULK","OTHER" }.Contains(kind))
                throw new ArgumentException("نوع بسته‌بندی نامعتبر است.");
            var id = Guid.NewGuid().ToString("N");
            using (var c = Open(path))
            using (var tx = c.BeginTransaction(IsolationLevel.Serializable))
            {
                var next = Convert.ToInt32(new SQLiteCommand("SELECT COALESCE(MAX(CAST(SUBSTR(packaging_code,4) AS INTEGER)),0)+1 FROM packaging_types WHERE packaging_code LIKE 'PKG%';", c, tx).ExecuteScalar());
                using (var cmd = new SQLiteCommand(@"INSERT INTO packaging_types(id,packaging_code,display_name,tare_weight_g,packaging_kind)
                    VALUES(@id,@code,@name,@tare,@kind);", c, tx))
                {
                    cmd.Parameters.AddWithValue("@id", id);
                    cmd.Parameters.AddWithValue("@code", "PKG" + next.ToString("000000"));
                    cmd.Parameters.AddWithValue("@name", name.Trim());
                    cmd.Parameters.AddWithValue("@tare", tareWeightGrams);
                    cmd.Parameters.AddWithValue("@kind", kind);
                    cmd.ExecuteNonQuery();
                }
                using (var audit = new SQLiteCommand(@"INSERT INTO audit_log(id,actor_user_id,action_code,entity_type,entity_id,after_json,machine_name)
                    VALUES(@id,@actor,'CREATE_QUICK','PACKAGING',@entity,@after,@machine);", c, tx))
                {
                    audit.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                    audit.Parameters.AddWithValue("@actor", (object)actorUserId ?? DBNull.Value);
                    audit.Parameters.AddWithValue("@entity", id);
                    audit.Parameters.AddWithValue("@after", "{\"display_name\":\"" + name.Trim().Replace("\\","\\\\").Replace("\"","\\\"") + "\",\"tare_weight_g\":" + tareWeightGrams + "}");
                    audit.Parameters.AddWithValue("@machine", Environment.MachineName);
                    audit.ExecuteNonQuery();
                }
                tx.Commit();
            }
            return id;
        }

        public static string CreateQuickProductGroup(string path, string name, string parentGroupId, string actorUserId)
        {
            if (String.IsNullOrWhiteSpace(name)) throw new ArgumentException("نام گروه محصول الزامی است.");
            var id = Guid.NewGuid().ToString("N");
            using (var c = Open(path))
            using (var tx = c.BeginTransaction(IsolationLevel.Serializable))
            {
                var next = Convert.ToInt32(new SQLiteCommand("SELECT COALESCE(MAX(CAST(SUBSTR(group_code,4) AS INTEGER)),0)+1 FROM product_groups WHERE group_code LIKE 'GRP%';", c, tx).ExecuteScalar());
                using (var cmd = new SQLiteCommand(@"INSERT INTO product_groups(id,group_code,parent_group_id,display_name,sort_order)
                    VALUES(@id,@code,@parent,@name,COALESCE((SELECT MAX(sort_order)+1 FROM product_groups WHERE parent_group_id IS @parent),0));", c, tx))
                {
                    cmd.Parameters.AddWithValue("@id", id);
                    cmd.Parameters.AddWithValue("@code", "GRP" + next.ToString("000000"));
                    cmd.Parameters.AddWithValue("@parent", (object)parentGroupId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@name", name.Trim());
                    cmd.ExecuteNonQuery();
                }
                using (var audit = new SQLiteCommand(@"INSERT INTO audit_log(id,actor_user_id,action_code,entity_type,entity_id,after_json,machine_name)
                    VALUES(@id,@actor,'CREATE_QUICK','PRODUCT_GROUP',@entity,@after,@machine);", c, tx))
                {
                    audit.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                    audit.Parameters.AddWithValue("@actor", (object)actorUserId ?? DBNull.Value);
                    audit.Parameters.AddWithValue("@entity", id);
                    audit.Parameters.AddWithValue("@after", "{\"display_name\":\"" + name.Trim().Replace("\\","\\\\").Replace("\"","\\\"") + "\"}");
                    audit.Parameters.AddWithValue("@machine", Environment.MachineName);
                    audit.ExecuteNonQuery();
                }
                tx.Commit();
            }
            return id;
        }

        public static List<LookupItem> ProductsByGroup(string path, string groupId)
        {
            var result = new List<LookupItem>();
            using (var c = Open(path))
            using (var cmd = new SQLiteCommand(@"SELECT id,product_code,display_name FROM products
                WHERE is_active=1 AND (@group IS NULL OR product_group_id=@group) ORDER BY display_name;", c))
            {
                cmd.Parameters.AddWithValue("@group", (object)groupId ?? DBNull.Value);
                using (var r = cmd.ExecuteReader())
                    while (r.Read()) result.Add(new LookupItem { Id=r.GetString(0), Code=r.GetString(1), Name=r.GetString(2) });
            }
            return result;
        }

        public static List<LookupItem> Warehouses(string path)
        {
            var result = new List<LookupItem>();
            using (var c = Open(path))
            using (var cmd = new SQLiteCommand("SELECT id,warehouse_code,display_name FROM warehouses WHERE is_active=1 ORDER BY display_name;", c))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) result.Add(new LookupItem { Id=r.GetString(0), Code=r.GetString(1), Name=r.GetString(2) });
            return result;
        }

        public static string CreateQuickWarehouse(string path, string name, string location, string actorUserId)
        {
            if (String.IsNullOrWhiteSpace(name)) throw new ArgumentException("نام انبار الزامی است.");
            var id=Guid.NewGuid().ToString("N");
            using(var c=Open(path))
            using(var tx=c.BeginTransaction(IsolationLevel.Serializable))
            {
                var next=Convert.ToInt32(new SQLiteCommand("SELECT COALESCE(MAX(CAST(SUBSTR(warehouse_code,2) AS INTEGER)),0)+1 FROM warehouses WHERE warehouse_code LIKE 'W%';",c,tx).ExecuteScalar());
                using(var cmd=new SQLiteCommand(@"INSERT INTO warehouses(id,warehouse_code,display_name,location_text)
                    VALUES(@id,@code,@name,@location);",c,tx))
                {
                    cmd.Parameters.AddWithValue("@id",id);
                    cmd.Parameters.AddWithValue("@code","W"+next.ToString("00"));
                    cmd.Parameters.AddWithValue("@name",name.Trim());
                    cmd.Parameters.AddWithValue("@location",(object)location ?? DBNull.Value);
                    cmd.ExecuteNonQuery();
                }
                using(var audit=new SQLiteCommand(@"INSERT INTO audit_log(id,actor_user_id,action_code,entity_type,entity_id,after_json,machine_name)
                    VALUES(@id,@actor,'CREATE_QUICK','WAREHOUSE',@entity,@after,@machine);",c,tx))
                {
                    audit.Parameters.AddWithValue("@id",Guid.NewGuid().ToString("N"));
                    audit.Parameters.AddWithValue("@actor",(object)actorUserId ?? DBNull.Value);
                    audit.Parameters.AddWithValue("@entity",id);
                    audit.Parameters.AddWithValue("@after","{\"display_name\":\""+name.Trim().Replace("\\","\\\\").Replace("\"","\\\"")+"\"}");
                    audit.Parameters.AddWithValue("@machine",Environment.MachineName);
                    audit.ExecuteNonQuery();
                }
                tx.Commit();
            }
            return id;
        }

        public static List<LookupItem> Units(string path)
        {
            var result = new List<LookupItem>();
            using (var c = Open(path))
            using (var cmd = new SQLiteCommand("SELECT id,unit_code,display_name FROM units WHERE is_active=1 ORDER BY display_name;", c))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) result.Add(new LookupItem { Id=r.GetString(0), Code=r.GetString(1), Name=r.GetString(2) });
            return result;
        }

        public static List<LookupItem> ProductGroups(string path)
        {
            var result = new List<LookupItem>();
            using (var c = Open(path))
            using (var cmd = new SQLiteCommand("SELECT id,group_code,display_name FROM product_groups WHERE is_active=1 ORDER BY sort_order,display_name;", c))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) result.Add(new LookupItem { Id=r.GetString(0), Code=r.GetString(1), Name=r.GetString(2) });
            return result;
        }

        // ------------------------------------------------------------------
        // Code-aware creation used by the Excel import commit engine (G6).
        // The caller's reference code is preserved verbatim; a duplicate code is
        // rejected rather than silently renumbered, so the owner's codes stay
        // authoritative.
        // ------------------------------------------------------------------
        public static string CreatePartyWithCode(string path, string code, string partyKind,
            string displayName, string nationalId, string economicCode, string mobile, string phone,
            string address, string notes, string actorUserId)
        {
            using (var c = Open(path))
            using (var tx = c.BeginTransaction(IsolationLevel.Serializable))
            {
                var id = CreatePartyWithCode(c, tx, code, partyKind, displayName, nationalId,
                    economicCode, mobile, phone, address, notes, actorUserId);
                tx.Commit();
                return id;
            }
        }

        /// <summary>
        /// Transactional core used by the import commit engine so that a party,
        /// its ledger account, its party account and the audit row land in the
        /// caller's transaction.
        /// </summary>
        internal static string CreatePartyWithCode(SQLiteConnection c, SQLiteTransaction tx,
            string code, string partyKind, string displayName, string nationalId, string economicCode,
            string mobile, string phone, string address, string notes, string actorUserId)
        {
            var normalizedCode = NormalizeReferenceCode(code, "کد شخص");
            var kind = (partyKind ?? String.Empty).Trim().ToUpperInvariant();
            if (kind != "PERSON" && kind != "COMPANY") kind = "PERSON";
            var name = (displayName ?? String.Empty).Trim();
            if (name.Length < 2) throw new ArgumentException("نام نمایشی شخص معتبر نیست.");

            var id = Guid.NewGuid().ToString("N");
            var ledgerId = Guid.NewGuid().ToString("N");
            var partyAccountId = Guid.NewGuid().ToString("N");
            {
                using (var duplicate = new SQLiteCommand(
                    "SELECT party_id FROM party_accounts WHERE ledger_account_id="
                    + "(SELECT id FROM ledger_accounts WHERE account_code=@code);", c, tx))
                {
                    duplicate.Parameters.AddWithValue("@code", normalizedCode);
                    var existing = Convert.ToString(duplicate.ExecuteScalar());
                    if (!String.IsNullOrEmpty(existing))
                        throw new InvalidOperationException("کد شخص «" + normalizedCode + "» قبلاً تعریف شده است.");
                }

                using (var cmd = new SQLiteCommand(@"INSERT INTO parties(id,party_kind,display_name,
                    national_id_enc,national_id_lookup_hmac,phone_enc,address_enc,notes)
                    VALUES(@id,@kind,@name,@nid,@hash,@phone,@address,@notes);", c, tx))
                {
                    cmd.Parameters.AddWithValue("@id", id);
                    cmd.Parameters.AddWithValue("@kind", kind);
                    cmd.Parameters.AddWithValue("@name", name);
                    cmd.Parameters.AddWithValue("@nid", (object)Protect(nationalId) ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@hash", (object)HashLookup(nationalId) ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@phone", (object)Protect(phone) ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@address", (object)Protect(address) ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@notes", (object)notes ?? DBNull.Value);
                    cmd.ExecuteNonQuery();
                }
                using (var cmd = new SQLiteCommand(@"INSERT INTO ledger_accounts(id,account_code,display_name,
                    account_class,normal_side,is_system_account) VALUES(@id,@code,@name,'CONTROL','DEBIT',0);", c, tx))
                {
                    cmd.Parameters.AddWithValue("@id", ledgerId);
                    cmd.Parameters.AddWithValue("@code", normalizedCode);
                    cmd.Parameters.AddWithValue("@name", name);
                    cmd.ExecuteNonQuery();
                }
                using (var cmd = new SQLiteCommand(
                    "INSERT INTO party_accounts(id,party_id,ledger_account_id,is_active) VALUES(@id,@party,@ledger,1);",
                    c, tx))
                {
                    cmd.Parameters.AddWithValue("@id", partyAccountId);
                    cmd.Parameters.AddWithValue("@party", id);
                    cmd.Parameters.AddWithValue("@ledger", ledgerId);
                    cmd.ExecuteNonQuery();
                }
                using (var audit = new SQLiteCommand(@"INSERT INTO audit_log(id,actor_user_id,action_code,
                    entity_type,entity_id,after_json,machine_name) VALUES(@id,@actor,'IMPORT_PARTY','PARTY',@entity,@after,@machine);",
                    c, tx))
                {
                    audit.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                    audit.Parameters.AddWithValue("@actor", (object)actorUserId ?? DBNull.Value);
                    audit.Parameters.AddWithValue("@entity", id);
                    audit.Parameters.AddWithValue("@after", SafeJson("party_code", normalizedCode, "display_name", name));
                    audit.Parameters.AddWithValue("@machine", Environment.MachineName);
                    audit.ExecuteNonQuery();
                }
            }
            return partyAccountId;
        }

        public static string CreateProductWithCode(string path, string code, string displayName,
            string groupCode, string unitCode, string actorUserId)
        {
            using (var c = Open(path))
            using (var tx = c.BeginTransaction(IsolationLevel.Serializable))
            {
                var id = CreateProductWithCode(c, tx, code, displayName, groupCode, unitCode, actorUserId);
                tx.Commit();
                return id;
            }
        }

        /// <summary>
        /// Transactional core used by the import commit engine.
        /// </summary>
        internal static string CreateProductWithCode(SQLiteConnection c, SQLiteTransaction tx,
            string code, string displayName, string groupCode, string unitCode, string actorUserId)
        {
            var normalizedCode = NormalizeReferenceCode(code, "کد محصول");
            var name = (displayName ?? String.Empty).Trim();
            if (name.Length < 2) throw new ArgumentException("نام محصول معتبر نیست.");
            var id = Guid.NewGuid().ToString("N");
            {
                using (var duplicate = new SQLiteCommand(
                    "SELECT id FROM products WHERE product_code=@code;", c, tx))
                {
                    duplicate.Parameters.AddWithValue("@code", normalizedCode);
                    if (Convert.ToString(duplicate.ExecuteScalar()) != null)
                        throw new InvalidOperationException("کد محصول «" + normalizedCode + "» قبلاً تعریف شده است.");
                }

                string groupId = null, unitId = null;
                if (!String.IsNullOrWhiteSpace(groupCode))
                {
                    using (var cmd = new SQLiteCommand(
                        "SELECT id FROM product_groups WHERE group_code=@code AND is_active=1;", c, tx))
                    {
                        cmd.Parameters.AddWithValue("@code", groupCode.Trim().ToUpperInvariant());
                        groupId = Convert.ToString(cmd.ExecuteScalar());
                    }
                    if (groupId == null) throw new ArgumentException("کد گروه محصول «" + groupCode + "» در فهرست وجود ندارد.");
                }
                if (!String.IsNullOrWhiteSpace(unitCode))
                {
                    using (var cmd = new SQLiteCommand(
                        "SELECT id FROM units WHERE unit_code=@code AND is_active=1;", c, tx))
                    {
                        cmd.Parameters.AddWithValue("@code", unitCode.Trim().ToUpperInvariant());
                        unitId = Convert.ToString(cmd.ExecuteScalar());
                    }
                    if (unitId == null) throw new ArgumentException("کد واحد «" + unitCode + "» در فهرست وجود ندارد.");
                }

                using (var cmd = new SQLiteCommand(@"INSERT INTO products(id,product_code,product_group_id,
                    display_name,product_role,unit_id) VALUES(@id,@code,@group,@name,'BOTH',@unit);", c, tx))
                {
                    cmd.Parameters.AddWithValue("@id", id);
                    cmd.Parameters.AddWithValue("@code", normalizedCode);
                    cmd.Parameters.AddWithValue("@group", (object)groupId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@name", name);
                    cmd.Parameters.AddWithValue("@unit", (object)unitId ?? DBNull.Value);
                    cmd.ExecuteNonQuery();
                }
                using (var audit = new SQLiteCommand(@"INSERT INTO audit_log(id,actor_user_id,action_code,
                    entity_type,entity_id,after_json,machine_name) VALUES(@id,@actor,'IMPORT_PRODUCT','PRODUCT',@entity,@after,@machine);",
                    c, tx))
                {
                    audit.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                    audit.Parameters.AddWithValue("@actor", (object)actorUserId ?? DBNull.Value);
                    audit.Parameters.AddWithValue("@entity", id);
                    audit.Parameters.AddWithValue("@after", SafeJson("product_code", normalizedCode, "display_name", name));
                    audit.Parameters.AddWithValue("@machine", Environment.MachineName);
                    audit.ExecuteNonQuery();
                }
            }
            return id;
        }

        private static string NormalizeReferenceCode(string value, string label)
        {
            var code = (value ?? String.Empty).Trim().ToUpperInvariant();
            if (code.Length < 2 || code.Length > 40 ||
                !System.Text.RegularExpressions.Regex.IsMatch(code, @"^[A-Z0-9][A-Z0-9._-]*$"))
                throw new ArgumentException(label + " باید فقط شامل حروف انگلیسی، عدد، خط تیره، زیرخط یا نقطه باشد.");
            return code;
        }

        private static string SafeJson(string key1, string value1, string key2, string value2)
        {
            return "{\"" + key1 + "\":\"" + (value1 ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") +
                   "\",\"" + key2 + "\":\"" + (value2 ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"}";
        }
    }
}
