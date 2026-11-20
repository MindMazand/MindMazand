using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.Globalization;

namespace Mazandasht.Desktop.Services
{
    public sealed class WeighingDraft
    {
        public string Direction { get; set; }
        public string PartyAccountId { get; set; }
        public string ProductId { get; set; }
        public string PackagingTypeId { get; set; }
        public string WarehouseId { get; set; }
        public string ScaleTicketNumber { get; set; }
        public long GrossWeightGrams { get; set; }
        public int BasketCount { get; set; }
        public int LossRateBasisPoints { get; set; }
        public string Notes { get; set; }
    }

    public static class WeighingService
    {
        public static string SaveDraft(string path, WeighingDraft draft, string userId)
        {
            if (draft == null) throw new ArgumentNullException("draft");
            if (draft.Direction != "IN" && draft.Direction != "OUT") throw new ArgumentException("جهت توزین نامعتبر است.");
            if (draft.GrossWeightGrams <= 0) throw new ArgumentException("وزن ناخالص باید بیشتر از صفر باشد.");
            if (draft.BasketCount < 0) throw new ArgumentException("تعداد سبد نمی‌تواند منفی باشد.");
            if (draft.LossRateBasisPoints < 0 || draft.LossRateBasisPoints > 10000) throw new ArgumentException("درصد افت باید بین صفر تا صد باشد.");
            if (String.IsNullOrWhiteSpace(draft.PartyAccountId))
                throw new ArgumentException("انتخاب طرف حساب برای ثبت باسکول الزامی است.");
            if (String.IsNullOrWhiteSpace(draft.ProductId) || String.IsNullOrWhiteSpace(draft.WarehouseId))
                throw new ArgumentException("انتخاب محصول و انبار برای ثبت باسکول الزامی است.");
            if (draft.BasketCount > 0 && String.IsNullOrWhiteSpace(draft.PackagingTypeId))
                throw new ArgumentException("برای ثبت تعداد سبد، نوع بسته‌بندی را نیز انتخاب کنید.");

            var id = Guid.NewGuid().ToString("N");
            using (var c = new SQLiteConnection("Data Source=" + path + ";Version=3;Pooling=True;"))
            {
                c.Open();
                using (var pragma = new SQLiteCommand("PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;", c)) pragma.ExecuteNonQuery();
                using (var tx = c.BeginTransaction(IsolationLevel.Serializable))
                {
                    long tarePerPackage = 0;
                    if (!String.IsNullOrWhiteSpace(draft.PackagingTypeId))
                    {
                        using (var cmd = new SQLiteCommand("SELECT tare_weight_g FROM packaging_types WHERE id=@id AND is_active=1;", c, tx))
                        {
                            cmd.Parameters.AddWithValue("@id", draft.PackagingTypeId);
                            var value = cmd.ExecuteScalar();
                            if (value == null) throw new InvalidOperationException("نوع بسته‌بندی انتخاب‌شده معتبر نیست.");
                            tarePerPackage = Convert.ToInt64(value);
                        }
                    }
                    var tare = checked(tarePerPackage * draft.BasketCount);
                    var preLossWeight = checked(draft.GrossWeightGrams - tare);
                    if (preLossWeight <= 0) throw new ArgumentException("وزن ظرف از وزن ناخالص بیشتر یا مساوی است.");
                    var loss = checked((long)Math.Round(preLossWeight * (draft.LossRateBasisPoints / 10000.0), MidpointRounding.AwayFromZero));
                    var net = checked(preLossWeight - loss);
                    if (net <= 0) throw new ArgumentException("وزن خالص باید بیشتر از صفر باشد.");

                    var operationCode = draft.Direction == "IN" ? "SYS-WEIGH-IN" : "SYS-WEIGH-OUT";
                    string operationId;
                    using (var cmd = new SQLiteCommand("SELECT id FROM operations WHERE operation_code=@code AND is_active=1;", c, tx))
                    {
                        cmd.Parameters.AddWithValue("@code", operationCode);
                        operationId = Convert.ToString(cmd.ExecuteScalar());
                    }
                    if (String.IsNullOrEmpty(operationId)) throw new InvalidOperationException("عملیات پایه توزین در دیتابیس تعریف نشده است.");

                    var fy = DateTime.Now.ToString("yyyy", CultureInfo.InvariantCulture);
                    int next;
                    using (var cmd = new SQLiteCommand(@"INSERT INTO document_sequences(document_type,fiscal_year,last_number)
                        VALUES('WEIGHING',@fy,1) ON CONFLICT(document_type,fiscal_year) DO UPDATE SET last_number=last_number+1;", c, tx))
                    {
                        cmd.Parameters.AddWithValue("@fy", fy);
                        cmd.ExecuteNonQuery();
                    }
                    using (var cmd = new SQLiteCommand("SELECT last_number FROM document_sequences WHERE document_type='WEIGHING' AND fiscal_year=@fy;", c, tx))
                    {
                        cmd.Parameters.AddWithValue("@fy", fy);
                        next = Convert.ToInt32(cmd.ExecuteScalar());
                    }
                    var docNo = "W-" + fy + "-" + next.ToString("000000");
                    var hasStock = true;
                    using (var cmd = new SQLiteCommand(@"INSERT INTO documents(id,document_type,fiscal_year,document_number,date_iso,time_local,status,
                        requires_journal,requires_stock_posting,party_account_id,created_by,reference_number,notes)
                        VALUES(@id,'WEIGHING',@fy,@no,@date,@time,'REVIEW',0,1,@party,@user,@ticket,@notes);", c, tx))
                    {
                        cmd.Parameters.AddWithValue("@id", id);
                        cmd.Parameters.AddWithValue("@fy", fy);
                        cmd.Parameters.AddWithValue("@no", docNo);
                        cmd.Parameters.AddWithValue("@date", DateTime.Now.ToString("yyyy-MM-dd"));
                        cmd.Parameters.AddWithValue("@time", DateTime.Now.ToString("HH:mm:ss"));
                        cmd.Parameters.AddWithValue("@party", (object)draft.PartyAccountId ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@user", userId);
                        cmd.Parameters.AddWithValue("@ticket", (object)draft.ScaleTicketNumber ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@notes", (object)draft.Notes ?? DBNull.Value);
                        cmd.ExecuteNonQuery();
                    }
                    using (var cmd = new SQLiteCommand(@"INSERT INTO weigh_tickets(document_id,operation_id,direction,party_account_id,product_id,
                        gross_weight_g,basket_count,tare_weight_g,loss_rate_bps,loss_weight_g,net_weight_g,warehouse_id,scale_ticket_number,notes)
                        VALUES(@id,@op,@direction,@party,@product,@gross,@baskets,@tare,@rate,@loss,@net,@warehouse,@ticket,@notes);", c, tx))
                    {
                        cmd.Parameters.AddWithValue("@id", id);
                        cmd.Parameters.AddWithValue("@op", operationId);
                        cmd.Parameters.AddWithValue("@direction", draft.Direction);
                        cmd.Parameters.AddWithValue("@party", (object)draft.PartyAccountId ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@product", draft.ProductId);
                        cmd.Parameters.AddWithValue("@gross", draft.GrossWeightGrams);
                        cmd.Parameters.AddWithValue("@baskets", draft.BasketCount);
                        cmd.Parameters.AddWithValue("@tare", tare);
                        cmd.Parameters.AddWithValue("@rate", draft.LossRateBasisPoints);
                        cmd.Parameters.AddWithValue("@loss", loss);
                        cmd.Parameters.AddWithValue("@net", net);
                        cmd.Parameters.AddWithValue("@warehouse", draft.WarehouseId);
                        cmd.Parameters.AddWithValue("@ticket", (object)draft.ScaleTicketNumber ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@notes", (object)draft.Notes ?? DBNull.Value);
                        cmd.ExecuteNonQuery();
                    }
                    if (!String.IsNullOrWhiteSpace(draft.PackagingTypeId) && draft.BasketCount > 0)
                    {
                        using (var cmd = new SQLiteCommand(@"INSERT INTO weigh_ticket_packaging(id,document_id,packaging_type_id,package_count,tare_weight_g_snapshot)
                            VALUES(@id,@doc,@pkg,@count,@tare);", c, tx))
                        {
                            cmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                            cmd.Parameters.AddWithValue("@doc", id);
                            cmd.Parameters.AddWithValue("@pkg", draft.PackagingTypeId);
                            cmd.Parameters.AddWithValue("@count", draft.BasketCount);
                            cmd.Parameters.AddWithValue("@tare", tarePerPackage);
                            cmd.ExecuteNonQuery();
                        }
                    }

                    if (draft.Direction == "IN")
                    {
                        var lotId = Guid.NewGuid().ToString("N");
                        var lotCode = docNo.Replace("-", "") + "-L1";
                        using (var cmd = new SQLiteCommand(@"INSERT INTO stock_lots(id,lot_code,product_id,origin_document_id,initial_quantity_g,notes)
                            VALUES(@id,@code,@product,@doc,@qty,'ایجاد خودکار از باسکول ورودی');", c, tx))
                        {
                            cmd.Parameters.AddWithValue("@id", lotId);
                            cmd.Parameters.AddWithValue("@code", lotCode);
                            cmd.Parameters.AddWithValue("@product", draft.ProductId);
                            cmd.Parameters.AddWithValue("@doc", id);
                            cmd.Parameters.AddWithValue("@qty", net);
                            cmd.ExecuteNonQuery();
                        }
                        using (var cmd = new SQLiteCommand(@"INSERT INTO stock_movements(id,movement_group_id,stock_lot_id,warehouse_id,source_document_id,
                            movement_type,quantity_delta_g,is_posted,notes)
                            VALUES(@id,@group,@lot,@warehouse,@doc,'RECEIPT',@qty,0,'در انتظار تأیید مدیر');", c, tx))
                        {
                            cmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                            cmd.Parameters.AddWithValue("@group", id);
                            cmd.Parameters.AddWithValue("@lot", lotId);
                            cmd.Parameters.AddWithValue("@warehouse", draft.WarehouseId);
                            cmd.Parameters.AddWithValue("@doc", id);
                            cmd.Parameters.AddWithValue("@qty", net);
                            cmd.ExecuteNonQuery();
                        }
                    }
                    else
                    {
                        var remaining = net;
                        using (var cmd = new SQLiteCommand(@"SELECT l.id, SUM(m.quantity_delta_g) AS available_g
                            FROM stock_lots l JOIN stock_movements m ON m.stock_lot_id=l.id
                            WHERE l.product_id=@product AND m.warehouse_id=@warehouse AND m.is_posted=1
                            GROUP BY l.id HAVING SUM(m.quantity_delta_g)>0 ORDER BY l.created_at_utc,l.lot_code;", c, tx))
                        {
                            cmd.Parameters.AddWithValue("@product", draft.ProductId);
                            cmd.Parameters.AddWithValue("@warehouse", draft.WarehouseId);
                            using (var r = cmd.ExecuteReader())
                            {
                                var allocations = new List<Tuple<string,long>>();
                                while (r.Read() && remaining > 0)
                                {
                                    var available = r.GetInt64(1);
                                    var take = Math.Min(available, remaining);
                                    allocations.Add(Tuple.Create(r.GetString(0), take));
                                    remaining -= take;
                                }
                                r.Close();
                                if (remaining > 0) throw new InvalidOperationException("موجودی تأییدشده این محصول در انبار برای خروج کافی نیست.");
                                foreach (var allocation in allocations)
                                {
                                    using (var move = new SQLiteCommand(@"INSERT INTO stock_movements(id,movement_group_id,stock_lot_id,warehouse_id,source_document_id,
                                        movement_type,quantity_delta_g,is_posted,notes)
                                        VALUES(@id,@group,@lot,@warehouse,@doc,'ISSUE',@qty,0,'خروج در انتظار تأیید مدیر');", c, tx))
                                    {
                                        move.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                                        move.Parameters.AddWithValue("@group", id);
                                        move.Parameters.AddWithValue("@lot", allocation.Item1);
                                        move.Parameters.AddWithValue("@warehouse", draft.WarehouseId);
                                        move.Parameters.AddWithValue("@doc", id);
                                        move.Parameters.AddWithValue("@qty", -allocation.Item2);
                                        move.ExecuteNonQuery();
                                    }
                                    using (var alloc = new SQLiteCommand(@"INSERT INTO stock_allocations(id,document_id,stock_lot_id,warehouse_id,quantity_g,allocation_kind)
                                        VALUES(@id,@doc,@lot,@warehouse,@qty,'WEIGH_OUT');", c, tx))
                                    {
                                        alloc.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                                        alloc.Parameters.AddWithValue("@doc", id);
                                        alloc.Parameters.AddWithValue("@lot", allocation.Item1);
                                        alloc.Parameters.AddWithValue("@warehouse", draft.WarehouseId);
                                        alloc.Parameters.AddWithValue("@qty", allocation.Item2);
                                        alloc.ExecuteNonQuery();
                                    }
                                }
                            }
                        }
                    }

                    using (var cmd = new SQLiteCommand(@"INSERT INTO document_status_history(id,document_id,old_status,new_status,changed_by,reason)
                        VALUES(@id,@doc,NULL,'REVIEW',@user,'ثبت باسکول و ایجاد گردش موجودی در انتظار تأیید');", c, tx))
                    {
                        cmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                        cmd.Parameters.AddWithValue("@doc", id);
                        cmd.Parameters.AddWithValue("@user", userId);
                        cmd.ExecuteNonQuery();
                    }
                    using (var audit = new SQLiteCommand(@"INSERT INTO audit_log(id,actor_user_id,action_code,entity_type,entity_id,after_json,machine_name)
                        VALUES(@id,@user,'CREATE_WEIGHING','DOCUMENT',@doc,@after,@machine);", c, tx))
                    {
                        audit.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
                        audit.Parameters.AddWithValue("@user", userId);
                        audit.Parameters.AddWithValue("@doc", id);
                        audit.Parameters.AddWithValue("@after", "{\"direction\":\"" + draft.Direction + "\",\"gross_weight_g\":" + draft.GrossWeightGrams + ",\"net_weight_g\":" + net + "}");
                        audit.Parameters.AddWithValue("@machine", Environment.MachineName);
                        audit.ExecuteNonQuery();
                    }
                    tx.Commit();
                }
            }
            return id;
        }
    }
}
