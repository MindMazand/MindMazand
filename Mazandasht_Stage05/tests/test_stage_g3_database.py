# -*- coding: utf-8 -*-
"""G3 acceptance tests: schema v6 increment, invariants and reporting views."""
from __future__ import annotations

import sqlite3
import tempfile
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[1]
APP = ROOT / "app" / "Mazandasht.Desktop"
DB = APP / "Database"
MIGRATIONS = ["schema_v1.sql", "system_seed.sql", "migration_002.sql", "migration_003.sql",
              "migration_004.sql", "migration_005.sql", "migration_006.sql", "migration_007.sql"]


def build_db() -> tuple[tempfile.TemporaryDirectory, sqlite3.Connection]:
    td = tempfile.TemporaryDirectory(prefix="mazandasht-g3-")
    con = sqlite3.connect(Path(td.name) / "g3.sqlite")
    con.execute("PRAGMA foreign_keys=ON")
    for name in MIGRATIONS:
        con.executescript((DB / name).read_text(encoding="utf-8"))
    return td, con


def seed_master(con: sqlite3.Connection) -> None:
    con.execute("INSERT INTO users(id,username,display_name,password_hash,password_salt,password_iterations,role_id) "
                "VALUES(?,?,?,?,?,?,?)",
                ("u1", "admin", "Admin", b"h", b"s", 600000, "role-admin"))
    con.execute("INSERT INTO parties(id,party_kind,display_name) VALUES('p1','PERSON','طرف آزمایشی')")
    con.execute("INSERT INTO ledger_accounts(id,account_code,display_name,account_class,normal_side) "
                "VALUES('la-p1','PTY000001','طرف آزمایشی','CONTROL','DEBIT')")
    con.execute("INSERT INTO party_accounts(id,party_id,ledger_account_id) VALUES('pa1','p1','la-p1')")
    con.execute("INSERT INTO product_groups(id,group_code,display_name) VALUES('pg1','GRP000001','مرکبات')")
    con.execute("INSERT INTO products(id,product_code,product_group_id,display_name,unit_id) "
                "VALUES('pr1','PRD000001','pg1','پرتقال','unit-kg')")
    con.execute("INSERT INTO warehouses(id,warehouse_code,display_name) VALUES('wh1','W01','انبار اصلی')")
    con.execute("INSERT INTO packaging_types(id,packaging_code,display_name,tare_weight_g,packaging_kind) "
                "VALUES('pk1','PKG000001','سبد',1500,'BASKET')")
    con.execute("INSERT INTO ledger_accounts(id,account_code,display_name,account_class,normal_side) "
                "VALUES('la-bank1','BANK000001','بانک آزمایشی','ASSET','DEBIT')")
    con.execute("INSERT INTO bank_accounts(id,ledger_account_id,display_title,bank_name) "
                "VALUES('ba1','la-bank1','بانک آزمایشی','بانک صادرات')")
    con.commit()


def insert_document(con, doc_id, doc_type="WEIGHING", number="T-1", status="REVIEW",
                    requires_journal=0, requires_stock=0, party="pa1"):
    con.execute("""INSERT INTO documents(id,document_type,fiscal_year,document_number,date_iso,time_local,status,
        requires_journal,requires_stock_posting,party_account_id,created_by)
        VALUES(?,?,?,?,?,?,?,?,?,?,?)""",
        (doc_id, doc_type, "1404", number, "2026-11-19", "10:00:00", status,
         requires_journal, requires_stock, party, "u1"))


# --------------------------------------------------------------------------
# migration & seed
# --------------------------------------------------------------------------
def test_migration_006_applies_and_is_healthy():
    td, con = build_db()
    try:
        assert con.execute("PRAGMA user_version").fetchone()[0] == 7
        assert con.execute("PRAGMA integrity_check").fetchone()[0] == "ok"
        assert con.execute("PRAGMA foreign_key_check").fetchall() == []
        assert con.execute("SELECT COUNT(*) FROM schema_migrations WHERE version=6").fetchone()[0] == 1
        assert con.execute("SELECT COUNT(*) FROM schema_migrations WHERE version=7").fetchone()[0] == 1
    finally:
        con.close(); td.cleanup()


def test_migration_006_is_idempotent():
    td, con = build_db()
    try:
        before = con.execute("SELECT COUNT(*) FROM ledger_accounts").fetchone()[0]
        con.executescript((DB / "migration_006.sql").read_text(encoding="utf-8"))
        after = con.execute("SELECT COUNT(*) FROM ledger_accounts").fetchone()[0]
        assert before == after == 16
        assert con.execute("SELECT COUNT(*) FROM permissions WHERE permission_code='database.reset'").fetchone()[0] == 1
    finally:
        con.close(); td.cleanup()


def test_default_chart_of_accounts_is_seeded():
    td, con = build_db()
    try:
        seed_master(con)
        rows = con.execute("SELECT account_code,account_class,normal_side FROM ledger_accounts "
                           "WHERE is_system_account=1 ORDER BY account_code").fetchall()
        assert len(rows) == 16
        classes = {r[1] for r in rows}
        assert {"ASSET", "LIABILITY", "EQUITY", "REVENUE", "EXPENSE"} <= classes
        codes = {r[0] for r in rows}
        assert {"1001", "2001", "3001", "4001", "5001"} <= codes
    finally:
        con.close(); td.cleanup()


def test_database_reset_permission_is_admin_only():
    td, con = build_db()
    try:
        rows = con.execute("SELECT role_id,granted FROM role_permissions WHERE permission_code='database.reset'").fetchall()
        assert rows == [("role-admin", 1)]
    finally:
        con.close(); td.cleanup()


# --------------------------------------------------------------------------
# integrity triggers
# --------------------------------------------------------------------------
def test_weighing_net_weight_trigger_rejects_mismatch():
    td, con = build_db()
    try:
        seed_master(con)
        insert_document(con, "d1")
        with pytest.raises(sqlite3.IntegrityError):
            con.execute("""INSERT INTO weigh_tickets(document_id,operation_id,direction,party_account_id,product_id,
                gross_weight_g,basket_count,tare_weight_g,loss_rate_bps,loss_weight_g,net_weight_g,warehouse_id)
                VALUES('d1','op-weigh-in','IN','pa1','pr1',100000,0,10000,0,5000,80000,'wh1')""")
        con.execute("""INSERT INTO weigh_tickets(document_id,operation_id,direction,party_account_id,product_id,
            gross_weight_g,basket_count,tare_weight_g,loss_rate_bps,loss_weight_g,net_weight_g,warehouse_id)
            VALUES('d1','op-weigh-in','IN','pa1','pr1',100000,0,10000,0,5000,85000,'wh1')""")
        con.commit()
    finally:
        con.close(); td.cleanup()


def test_invoice_line_pricing_trigger_enforces_half_up_rounding():
    td, con = build_db()
    try:
        seed_master(con)
        insert_document(con, "buy1", doc_type="PURCHASE")
        # 1234 g * 1000 irr/kg = 1,234,000 / 1000 = 1234 exactly.
        con.execute("""INSERT INTO purchase_invoice_lines(id,document_id,line_number,product_id,quantity_g,
            unit_price_irr_per_kg,gross_amount_irr,net_amount_irr)
            VALUES('pl1','buy1',1,'pr1',1234,1000,1234,1234)""")
        # 1 g * 1500 irr/kg = 1500/1000 -> half-up = 2 (not 1).
        insert_document(con, "buy2", doc_type="PURCHASE", number="T-2")
        con.execute("""INSERT INTO purchase_invoice_lines(id,document_id,line_number,product_id,quantity_g,
            unit_price_irr_per_kg,gross_amount_irr,net_amount_irr)
            VALUES('pl2','buy2',1,'pr1',1,1500,2,2)""")
        with pytest.raises(sqlite3.IntegrityError):
            insert_document(con, "buy3", doc_type="PURCHASE", number="T-3")
            con.execute("""INSERT INTO purchase_invoice_lines(id,document_id,line_number,product_id,quantity_g,
                unit_price_irr_per_kg,gross_amount_irr,net_amount_irr)
                VALUES('pl3','buy3',1,'pr1',1,1500,1,1)""")
        con.commit()
    finally:
        con.close(); td.cleanup()


def test_sales_line_pricing_trigger():
    td, con = build_db()
    try:
        seed_master(con)
        insert_document(con, "s1", doc_type="SALE")
        con.execute("""INSERT INTO sales_invoice_lines(id,document_id,line_number,product_id,quantity_g,
            unit_price_irr_per_kg,gross_amount_irr,net_receivable_irr)
            VALUES('sl1','s1',1,'pr1',5000,2000,10000,10000)""")
        with pytest.raises(sqlite3.IntegrityError):
            con.execute("""INSERT INTO sales_invoice_lines(id,document_id,line_number,product_id,quantity_g,
                unit_price_irr_per_kg,gross_amount_irr,net_receivable_irr)
                VALUES('sl2','s1',2,'pr1',5000,2000,10000,9999)""")
        con.commit()
    finally:
        con.close(); td.cleanup()


def test_sorting_reconciliation_is_computed_automatically():
    td, con = build_db()
    try:
        seed_master(con)
        insert_document(con, "d-lot")
        con.execute("INSERT INTO stock_lots(id,lot_code,product_id,origin_document_id,initial_quantity_g) "
                    "VALUES('lot-x','LOTX','pr1','d-lot',100000)")
        con.execute("INSERT INTO stock_lots(id,lot_code,product_id,origin_document_id,initial_quantity_g) "
                    "VALUES('lot-y','LOTY','pr1','d-lot',200000)")
        insert_document(con, "sort1", doc_type="SORTING")
        con.execute("""INSERT INTO sorting_batches(document_id,input_weight_g,output_weight_g,waste_weight_g,
            process_loss_weight_g,variance_weight_g,variance_tolerance_g,reconciliation_status)
            VALUES('sort1',100000,0,0,0,0,1000,'PENDING')""")
        con.execute("""INSERT INTO sorting_batch_inputs(id,sorting_document_id,stock_lot_id,quantity_g)
            VALUES('si1','sort1','lot-x',100000)""")
        status = con.execute("SELECT reconciliation_status FROM sorting_batches WHERE document_id='sort1'").fetchone()[0]
        assert status == "WARNING"  # no outputs yet -> variance = 100000 > 1000

        con.execute("""INSERT INTO sorting_outputs(id,sorting_document_id,product_id,stock_lot_id,quantity_g,line_number)
            VALUES('so1','sort1','pr1','lot-y',99000,1)""")
        row = con.execute("""SELECT output_weight_g,variance_weight_g,reconciliation_status
            FROM sorting_batches WHERE document_id='sort1'""").fetchone()
        assert row[0] == 99000
        assert row[1] == 1000
        assert row[2] == "MATCHED"
        con.commit()
    finally:
        con.close(); td.cleanup()


def test_cheque_clearance_requires_full_amount():
    td, con = build_db()
    try:
        seed_master(con)
        insert_document(con, "c1", doc_type="CHEQUE")
        con.execute("""INSERT INTO cheques(id,document_id,cheque_number,bank_account_id,direction,issue_date_iso,
            due_date_iso,amount_irr) VALUES('ch1','c1','1001','ba1','OUT','2026-11-19','2026-12-19',500000)""")
        with pytest.raises(sqlite3.IntegrityError):
            con.execute("""INSERT INTO cheque_events(id,cheque_id,event_type,event_date_iso,amount_irr,created_by)
                VALUES('e1','ch1','CLEARED','2026-11-20',499999,'u1')""")
        con.execute("""INSERT INTO cheque_events(id,cheque_id,event_type,event_date_iso,amount_irr,created_by)
            VALUES('e2','ch1','CLEARED','2026-11-20',500000,'u1')""")
        con.commit()
    finally:
        con.close(); td.cleanup()


def test_document_number_autofill_is_sequential_and_unique():
    td, con = build_db()
    try:
        seed_master(con)
        insert_document(con, "n1", number="")
        insert_document(con, "n2", number="")
        numbers = [r[0] for r in con.execute("SELECT document_number FROM documents ORDER BY id")]
        assert numbers == ["1", "2"]
        seq = con.execute("SELECT last_number FROM document_sequences WHERE document_type='WEIGHING' AND fiscal_year='1404'").fetchone()[0]
        assert seq == 2
    finally:
        con.close(); td.cleanup()


# --------------------------------------------------------------------------
# settlement allocation
# --------------------------------------------------------------------------
def test_settlement_allocation_guards():
    td, con = build_db()
    try:
        seed_master(con)
        insert_document(con, "pay1", doc_type="BANK_TXN")
        con.execute("""INSERT INTO payment_transactions(document_id,bank_account_id,party_account_id,direction,amount_irr)
            VALUES('pay1','ba1','pa1','OUT',1000000)""")
        insert_document(con, "inv1", doc_type="SALE", number="INV-1")
        insert_document(con, "inv2", doc_type="SALE", number="INV-2")

        con.execute("""INSERT INTO settlement_allocations(id,payment_document_id,invoice_document_id,amount_irr,allocated_by)
            VALUES('sa1','pay1','inv1',600000,'u1')""")
        with pytest.raises(sqlite3.IntegrityError):
            con.execute("""INSERT INTO settlement_allocations(id,payment_document_id,invoice_document_id,amount_irr,allocated_by)
                VALUES('sa2','pay1','inv2',500000,'u1')""")
        con.execute("""INSERT INTO settlement_allocations(id,payment_document_id,invoice_document_id,amount_irr,allocated_by)
            VALUES('sa3','pay1','inv2',400000,'u1')""")
        with pytest.raises(sqlite3.IntegrityError):
            con.execute("""INSERT INTO settlement_allocations(id,payment_document_id,invoice_document_id,amount_irr,allocated_by)
                VALUES('sa4','inv1','inv2',1,'u1')""")
        con.commit()
    finally:
        con.close(); td.cleanup()


# --------------------------------------------------------------------------
# reporting views
# --------------------------------------------------------------------------
def test_reporting_views_return_data():
    td, con = build_db()
    try:
        seed_master(con)
        # A posted journal on the party control account feeds v_party_balance.
        insert_document(con, "j1", doc_type="SALE", number="J-1", status="APPROVED", requires_journal=1)
        con.execute("""INSERT INTO journal_entries(id,journal_number,document_id,entry_date_iso,description,is_posted)
            VALUES('je1','JN-1','j1','2026-11-19','سند آزمایشی',0)""")
        con.execute("""INSERT INTO journal_lines(id,journal_entry_id,line_number,ledger_account_id,debit_irr,credit_irr)
            VALUES('jl1','je1',1,'la-p1',100000,0)""")
        con.execute("""INSERT INTO journal_lines(id,journal_entry_id,line_number,ledger_account_id,debit_irr,credit_irr)
            VALUES('jl2','je1',2,'la-revenue',0,100000)""")
        con.execute("UPDATE journal_entries SET is_posted=1, posted_by='u1' WHERE id='je1'")
        con.commit()

        balance = con.execute("SELECT balance_irr FROM v_party_balance WHERE party_account_id='pa1'").fetchone()[0]
        assert balance == 100000
        profit = con.execute("SELECT total_credit_irr FROM v_profit_loss WHERE account_code='4001'").fetchone()[0]
        assert profit == 100000
        assert con.execute("SELECT COUNT(*) FROM v_stock_on_hand").fetchone()[0] == 0
        assert con.execute("SELECT COUNT(*) FROM v_cheque_register").fetchone()[0] == 0
        assert con.execute("SELECT COUNT(*) FROM v_bank_balance").fetchone()[0] == 1
    finally:
        con.close(); td.cleanup()
