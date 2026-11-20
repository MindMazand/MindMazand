import sqlite3
import tempfile
from pathlib import Path
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
APP = ROOT / "app" / "Mazandasht.Desktop"

def build_db():
    td = tempfile.TemporaryDirectory()
    db = Path(td.name) / "test.sqlite"
    c = sqlite3.connect(db)
    c.execute("PRAGMA foreign_keys=ON")
    for f in ["schema_v1.sql", "system_seed.sql", "migration_002.sql", "migration_003.sql", "migration_004.sql"]:
        c.executescript((APP / "Database" / f).read_text(encoding="utf-8"))
    c.execute("INSERT INTO users(id,username,display_name,password_hash,password_salt,password_iterations,role_id) VALUES(?,?,?,?,?,?,?)",
              ("admin1","admin","Admin",b"h",b"s",600000,"role-admin"))
    c.execute("INSERT INTO users(id,username,display_name,password_hash,password_salt,password_iterations,role_id) VALUES(?,?,?,?,?,?,?)",
              ("manager1","manager","Manager",b"h",b"s",600000,"role-manager"))
    c.execute("INSERT INTO users(id,username,display_name,password_hash,password_salt,password_iterations,role_id) VALUES(?,?,?,?,?,?,?)",
              ("scale1","scale","Scale",b"h",b"s",600000,"role-scale"))
    c.execute("INSERT INTO parties(id,party_kind,display_name) VALUES('p1','PERSON','طرف آزمایشی')")
    c.execute("INSERT INTO ledger_accounts(id,account_code,display_name,account_class,normal_side) VALUES('la1','PTY000001','طرف آزمایشی','CONTROL','DEBIT')")
    c.execute("INSERT INTO party_accounts(id,party_id,ledger_account_id) VALUES('pa1','p1','la1')")
    c.execute("INSERT INTO product_groups(id,group_code,display_name) VALUES('pg1','GRP000001','مرکبات')")
    c.execute("INSERT INTO products(id,product_code,product_group_id,display_name,unit_id) VALUES('pr1','PRD000001','pg1','پرتقال','unit-kg')")
    c.execute("INSERT INTO warehouses(id,warehouse_code,display_name) VALUES('wh1','W01','انبار اصلی')")
    c.execute("INSERT INTO packaging_types(id,packaging_code,display_name,tare_weight_g,packaging_kind) VALUES('pk1','PKG000001','سبد',1500,'BASKET')")
    c.commit()
    return td, c, db

def insert_weigh_doc(c, doc_id, number, direction, grams, requires_stock=1):
    op = "op-weigh-in" if direction == "IN" else "op-weigh-out"
    c.execute("""INSERT INTO documents(id,document_type,fiscal_year,document_number,date_iso,time_local,status,
              requires_journal,requires_stock_posting,party_account_id,created_by)
              VALUES(?, 'WEIGHING','2026',?,'2026-11-15','10:00:00','REVIEW',0,1,'pa1','manager1')""",(doc_id,number))
    c.execute("""INSERT INTO weigh_tickets(document_id,operation_id,direction,party_account_id,product_id,gross_weight_g,
              basket_count,tare_weight_g,loss_rate_bps,loss_weight_g,net_weight_g,warehouse_id)
              VALUES(?,?,?,?,?, ?,0,0,0,0,?,'wh1')""",(doc_id,op,direction,"pa1","pr1",grams,grams))

def test_stage04_migration_and_lookup_schema():
    td,c,db=build_db()
    try:
        assert c.execute("PRAGMA user_version").fetchone()[0] == 4
        assert c.execute("SELECT COUNT(*) FROM operations WHERE operation_code IN ('SYS-WEIGH-IN','SYS-WEIGH-OUT')").fetchone()[0] == 2
        assert c.execute("SELECT COUNT(*) FROM units WHERE unit_code IN ('KG','G')").fetchone()[0] == 2
        assert c.execute("PRAGMA integrity_check").fetchone()[0] == "ok"
        assert c.execute("PRAGMA foreign_key_check").fetchall() == []
    finally:
        c.close(); td.cleanup()

def test_inbound_and_outbound_stock_are_approval_gated_and_nonnegative():
    td,c,db=build_db()
    try:
        insert_weigh_doc(c,"in1","W-2026-000001","IN",100000)
        c.execute("""INSERT INTO stock_lots(id,lot_code,product_id,origin_document_id,initial_quantity_g)
                     VALUES('lot1','LOT-1','pr1','in1',100000)""")
        c.execute("""INSERT INTO stock_movements(id,movement_group_id,stock_lot_id,warehouse_id,source_document_id,movement_type,quantity_delta_g,is_posted)
                     VALUES('m-in','in1','lot1','wh1','in1','RECEIPT',100000,0)""")
        # Approval trigger requires pending stock; stock cannot be posted before approval.
        try:
            c.execute("UPDATE stock_movements SET is_posted=1 WHERE id='m-in'")
            assert False, "movement posted before approval"
        except sqlite3.IntegrityError:
            c.rollback()
        # Rebuild inbound after rollback (transaction rolled back all previous inserts).
        c.close()
        td,c,db=build_db()
        insert_weigh_doc(c,"in1","W-2026-000001","IN",100000)
        c.execute("INSERT INTO stock_lots(id,lot_code,product_id,origin_document_id,initial_quantity_g) VALUES('lot1','LOT-1','pr1','in1',100000)")
        c.execute("""INSERT INTO stock_movements(id,movement_group_id,stock_lot_id,warehouse_id,source_document_id,movement_type,quantity_delta_g,is_posted)
                     VALUES('m-in','in1','lot1','wh1','in1','RECEIPT',100000,0)""")
        c.execute("UPDATE documents SET status='APPROVED',approved_by='manager1',approved_at_utc='2026-11-15T10:00:00Z' WHERE id='in1'")
        c.execute("UPDATE stock_movements SET is_posted=1 WHERE id='m-in'")
        assert c.execute("SELECT SUM(quantity_delta_g) FROM stock_movements WHERE stock_lot_id='lot1' AND is_posted=1").fetchone()[0] == 100000

        insert_weigh_doc(c,"out1","W-2026-000002","OUT",30000)
        c.execute("""INSERT INTO stock_movements(id,movement_group_id,stock_lot_id,warehouse_id,source_document_id,movement_type,quantity_delta_g,is_posted)
                     VALUES('m-out','out1','lot1','wh1','out1','ISSUE',-30000,0)""")
        c.execute("UPDATE documents SET status='APPROVED',approved_by='manager1',approved_at_utc='2026-11-15T11:00:00Z' WHERE id='out1'")
        c.execute("UPDATE stock_movements SET is_posted=1 WHERE id='m-out'")
        assert c.execute("SELECT SUM(quantity_delta_g) FROM stock_movements WHERE stock_lot_id='lot1' AND is_posted=1").fetchone()[0] == 70000

        insert_weigh_doc(c,"out2","W-2026-000003","OUT",90000)
        c.execute("""INSERT INTO stock_movements(id,movement_group_id,stock_lot_id,warehouse_id,source_document_id,movement_type,quantity_delta_g,is_posted)
                     VALUES('m-out2','out2','lot1','wh1','out2','ISSUE',-90000,0)""")
        c.execute("UPDATE documents SET status='APPROVED',approved_by='manager1',approved_at_utc='2026-11-15T12:00:00Z' WHERE id='out2'")
        try:
            c.execute("UPDATE stock_movements SET is_posted=1 WHERE id='m-out2'")
            assert False, "negative stock was accepted"
        except sqlite3.IntegrityError as ex:
            assert "Insufficient stock" in str(ex)
        assert c.execute("PRAGMA integrity_check").fetchone()[0] == "ok"
    finally:
        c.close(); td.cleanup()

def test_manager_approval_and_ui_contracts():
    for x in ["MainWindow.xaml","App.xaml"]:
        ET.parse(APP/x)
    code=(APP/"Services"/"ApprovalService.cs").read_text(encoding="utf-8")
    weigh=(APP/"Services"/"WeighingService.cs").read_text(encoding="utf-8")
    master=(APP/"Services"/"MasterDataService.cs").read_text(encoding="utf-8")
    ui=(APP/"Views"/"OperationalWindows.cs").read_text(encoding="utf-8")
    assert 'roleCode != "ADMIN" && roleCode != "MANAGER"' in code
    assert "trg_stock_movement_no_negative_balance_before_post" in (APP/"Database"/"migration_003.sql").read_text(encoding="utf-8")
    assert "Insufficient stock balance" in (APP/"Database"/"migration_003.sql").read_text(encoding="utf-8")
    assert "ProductsByGroup" in master and "Resolve(_product,false)" in ui
    assert "ProtectedData.Protect" in master and "HMACSHA256" in master
    assert "stock_allocations" in weigh and "IsolationLevel.Serializable" in weigh
    assert "LoadDailyFlow" in (APP/"Services"/"DashboardQueryService.cs").read_text(encoding="utf-8")
