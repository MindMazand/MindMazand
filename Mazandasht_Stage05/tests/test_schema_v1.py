from __future__ import annotations
import sqlite3
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1] / 'app' / 'Mazandasht.Desktop' / 'Database'
SCHEMA = ROOT / 'schema_v1.sql'
SEED = ROOT / 'system_seed.sql'


def expect_integrity_error(fn, contains: str = ''):
    try:
        fn()
    except sqlite3.IntegrityError as exc:
        if contains and contains.lower() not in str(exc).lower():
            raise AssertionError(f'Expected error containing {contains!r}, got {exc!r}') from exc
        return
    raise AssertionError('Expected sqlite3.IntegrityError, but operation succeeded')


def main():
    with tempfile.TemporaryDirectory(prefix='mazandasht-schema-') as td:
        db = Path(td) / 'stage01.sqlite'
        con = sqlite3.connect(db)
        con.execute('PRAGMA foreign_keys=ON')
        con.executescript(SCHEMA.read_text(encoding='utf-8'))
        con.executescript(SEED.read_text(encoding='utf-8'))
        assert con.execute('PRAGMA foreign_keys').fetchone()[0] == 1
        assert con.execute('PRAGMA user_version').fetchone()[0] == 1
        assert con.execute('SELECT COUNT(*) FROM roles').fetchone()[0] == 6
        assert con.execute('SELECT COUNT(*) FROM users').fetchone()[0] == 0, 'No default/demo user may be created'
        assert con.execute('SELECT COUNT(*) FROM parties').fetchone()[0] == 0, 'No demo parties may be created'
        assert con.execute("SELECT granted FROM role_permissions WHERE role_id='role-manager' AND permission_code='documents.approve'").fetchone()[0] == 1
        assert con.execute("SELECT COUNT(*) FROM role_permissions WHERE role_id='role-manager' AND permission_code='users.manage'").fetchone()[0] == 0

        # Minimal identity and accounts for tests only.
        con.execute("INSERT INTO users(id,username,display_name,password_hash,password_salt,password_iterations,role_id) VALUES(?,?,?,?,?,?,?)",
                    ('u-test','test-admin','Test Admin',b'hash',b'salt',600000,'role-admin'))
        con.execute("INSERT INTO ledger_accounts(id,account_code,display_name,account_class,normal_side) VALUES(?,?,?,?,?)",
                    ('la-bank','BANKTEST','Test Bank','ASSET','DEBIT'))
        con.execute("INSERT INTO ledger_accounts(id,account_code,display_name,account_class,normal_side) VALUES(?,?,?,?,?)",
                    ('la-party','CTEST','Test Party','LIABILITY','CREDIT'))

        # A balanced journal: document approval is first; journal posting is second, in one DB transaction.
        con.execute("INSERT INTO documents(id,document_type,fiscal_year,document_number,date_iso,date_jalali,status,requires_journal,created_by) VALUES(?,?,?,?,?,?,?,?,?)",
                    ('doc-ok','BANK_TXN','1405','TX-0001','2026-11-14','1405/08/23','REVIEW',1,'u-test'))
        con.execute("INSERT INTO journal_entries(id,journal_number,document_id,entry_date_iso,description,is_posted) VALUES(?,?,?,?,?,0)",
                    ('je-ok','JV-0001','doc-ok','2026-11-14','Balanced test entry'))
        con.execute("INSERT INTO journal_lines(id,journal_entry_id,line_number,ledger_account_id,debit_irr,credit_irr) VALUES(?,?,?,?,?,?)",
                    ('jl-ok-1','je-ok',1,'la-bank',100000,0))
        con.execute("INSERT INTO journal_lines(id,journal_entry_id,line_number,ledger_account_id,debit_irr,credit_irr) VALUES(?,?,?,?,?,?)",
                    ('jl-ok-2','je-ok',2,'la-party',0,100000))
        con.execute("UPDATE documents SET status='APPROVED', approved_by='u-test', approved_at_utc='2026-11-14T00:00:00Z' WHERE id='doc-ok'")
        con.execute("UPDATE journal_entries SET is_posted=1, posted_by='u-test', posted_at_utc='2026-11-14T00:00:00Z' WHERE id='je-ok'")
        trial = dict((r[0], (r[1], r[2])) for r in con.execute('SELECT account_code,total_debit_irr,total_credit_irr FROM v_trial_balance'))
        assert trial['BANKTEST'] == (100000, 0)
        assert trial['CTEST'] == (0, 100000)

        # Approved financial documents can only be voided after a posted reversal journal.
        con.execute("INSERT INTO documents(id,document_type,fiscal_year,document_number,date_iso,status,requires_journal,created_by) VALUES(?,?,?,?,?,?,?,?)",
                    ('doc-reversal','ADJUSTMENT','1405','RV-0001','2026-11-14','REVIEW',1,'u-test'))
        con.execute("INSERT INTO journal_entries(id,journal_number,document_id,entry_date_iso,description,is_posted,reversal_of_entry_id) VALUES(?,?,?,?,?,0,?)",
                    ('je-reversal','JV-R0001','doc-reversal','2026-11-14','Reversal test entry','je-ok'))
        con.execute("INSERT INTO journal_lines(id,journal_entry_id,line_number,ledger_account_id,debit_irr,credit_irr) VALUES(?,?,?,?,?,?)",
                    ('jl-r-1','je-reversal',1,'la-bank',0,100000))
        con.execute("INSERT INTO journal_lines(id,journal_entry_id,line_number,ledger_account_id,debit_irr,credit_irr) VALUES(?,?,?,?,?,?)",
                    ('jl-r-2','je-reversal',2,'la-party',100000,0))
        con.execute("UPDATE documents SET status='APPROVED', approved_by='u-test', approved_at_utc='2026-11-14T00:01:00Z' WHERE id='doc-reversal'")
        con.execute("UPDATE journal_entries SET is_posted=1, posted_by='u-test', posted_at_utc='2026-11-14T00:01:00Z' WHERE id='je-reversal'")
        con.execute("UPDATE documents SET status='VOIDED', voided_by='u-test', voided_at_utc='2026-11-14T00:02:00Z', void_reason='Test reversal' WHERE id='doc-ok'")

        # An unbalanced journal cannot approve or post.
        con.execute("INSERT INTO documents(id,document_type,fiscal_year,document_number,date_iso,status,requires_journal,created_by) VALUES(?,?,?,?,?,?,?,?)",
                    ('doc-bad','BANK_TXN','1405','TX-0002','2026-11-14','REVIEW',1,'u-test'))
        con.execute("INSERT INTO journal_entries(id,journal_number,document_id,entry_date_iso,description,is_posted) VALUES(?,?,?,?,?,0)",
                    ('je-bad','JV-0002','doc-bad','2026-11-14','Unbalanced test entry'))
        con.execute("INSERT INTO journal_lines(id,journal_entry_id,line_number,ledger_account_id,debit_irr,credit_irr) VALUES(?,?,?,?,?,?)",
                    ('jl-bad-1','je-bad',1,'la-bank',100000,0))
        con.execute("INSERT INTO journal_lines(id,journal_entry_id,line_number,ledger_account_id,debit_irr,credit_irr) VALUES(?,?,?,?,?,?)",
                    ('jl-bad-2','je-bad',2,'la-party',0,90000))
        expect_integrity_error(lambda: con.execute("UPDATE documents SET status='APPROVED' WHERE id='doc-bad'"), 'balanced journal')
        expect_integrity_error(lambda: con.execute("UPDATE journal_entries SET is_posted=1 WHERE id='je-bad'"), 'approved document')

        # A financial document cannot be approved without a journal.
        con.execute("INSERT INTO documents(id,document_type,fiscal_year,document_number,date_iso,status,requires_journal,created_by) VALUES(?,?,?,?,?,?,?,?)",
                    ('doc-no-journal','BANK_TXN','1405','TX-0003','2026-11-14','REVIEW',1,'u-test'))
        expect_integrity_error(lambda: con.execute("UPDATE documents SET status='APPROVED' WHERE id='doc-no-journal'"), 'balanced journal')

        # Stock posting is also held until approval.
        con.execute("INSERT INTO units(id,unit_code,display_name) VALUES('unit-kg','KG','کیلوگرم')")
        con.execute("INSERT INTO product_groups(id,group_code,display_name) VALUES('pg-citrus','PG01','مرکبات')")
        con.execute("INSERT INTO products(id,product_code,product_group_id,display_name,product_role,unit_id) VALUES('prod-orange','PBTEST','pg-citrus','پرتقال','BUY','unit-kg')")
        con.execute("INSERT INTO warehouses(id,warehouse_code,display_name) VALUES('wh-main','WTEST','انبار تست')")
        con.execute("INSERT INTO documents(id,document_type,fiscal_year,document_number,date_iso,status,requires_stock_posting,created_by) VALUES(?,?,?,?,?,?,?,?)",
                    ('doc-stock','WEIGHING','1405','WI-0001','2026-11-14','REVIEW',1,'u-test'))
        con.execute("INSERT INTO stock_lots(id,lot_code,product_id,origin_document_id,initial_quantity_g) VALUES(?,?,?,?,?)",
                    ('lot-1','LOT-TEST-1','prod-orange','doc-stock',250000))
        con.execute("INSERT INTO stock_movements(id,movement_group_id,stock_lot_id,warehouse_id,source_document_id,movement_type,quantity_delta_g,is_posted) VALUES(?,?,?,?,?,?,?,0)",
                    ('sm-1','mg-1','lot-1','wh-main','doc-stock','RECEIPT',250000))
        con.execute("UPDATE documents SET status='APPROVED', approved_by='u-test', approved_at_utc='2026-11-14T00:00:00Z' WHERE id='doc-stock'")
        con.execute("UPDATE stock_movements SET is_posted=1 WHERE id='sm-1'")
        assert con.execute("SELECT quantity_g FROM v_stock_balance WHERE stock_lot_id='lot-1'").fetchone()[0] == 250000
        expect_integrity_error(lambda: con.execute("UPDATE stock_movements SET quantity_delta_g=1 WHERE id='sm-1'"), 'immutable')

        # An approved stock document can only be voided after a posted reversing movement.
        con.execute("INSERT INTO documents(id,document_type,fiscal_year,document_number,date_iso,status,requires_stock_posting,created_by) VALUES(?,?,?,?,?,?,?,?)",
                    ('doc-stock-rev','ADJUSTMENT','1405','RV-ST-0001','2026-11-14','REVIEW',1,'u-test'))
        con.execute("INSERT INTO stock_movements(id,movement_group_id,stock_lot_id,warehouse_id,source_document_id,movement_type,quantity_delta_g,is_posted,reversal_of_movement_id) VALUES(?,?,?,?,?,?,?,0,?)",
                    ('sm-rev-1','mg-rev-1','lot-1','wh-main','doc-stock-rev','REVERSAL',-250000,'sm-1'))
        con.execute("UPDATE documents SET status='APPROVED', approved_by='u-test', approved_at_utc='2026-11-14T00:03:00Z' WHERE id='doc-stock-rev'")
        con.execute("UPDATE stock_movements SET is_posted=1 WHERE id='sm-rev-1'")
        con.execute("UPDATE documents SET status='VOIDED', voided_by='u-test', voided_at_utc='2026-11-14T00:04:00Z', void_reason='Test stock reversal' WHERE id='doc-stock'")
        assert con.execute("SELECT COUNT(*) FROM v_stock_balance WHERE stock_lot_id='lot-1'").fetchone()[0] == 0

        # A stock-required document cannot approve without pending movements.
        con.execute("INSERT INTO documents(id,document_type,fiscal_year,document_number,date_iso,status,requires_stock_posting,created_by) VALUES(?,?,?,?,?,?,?,?)",
                    ('doc-no-stock','WEIGHING','1405','WI-0002','2026-11-14','REVIEW',1,'u-test'))
        expect_integrity_error(lambda: con.execute("UPDATE documents SET status='APPROVED' WHERE id='doc-no-stock'"), 'stock movements')

        # Posted journal lines and audit log are immutable.
        expect_integrity_error(lambda: con.execute("INSERT INTO journal_lines(id,journal_entry_id,line_number,ledger_account_id,debit_irr,credit_irr) VALUES('jl-late','je-ok',3,'la-bank',1,0)"), 'immutable')
        con.execute("INSERT INTO audit_log(id,actor_user_id,action_code,entity_type,entity_id) VALUES('a1','u-test','CREATE','TEST','1')")
        expect_integrity_error(lambda: con.execute("UPDATE audit_log SET action_code='ALTER' WHERE id='a1'"), 'append-only')
        expect_integrity_error(lambda: con.execute("DELETE FROM audit_log WHERE id='a1'"), 'append-only')

        con.commit()
        assert con.execute('PRAGMA integrity_check').fetchone()[0] == 'ok'
        assert con.execute('PRAGMA foreign_key_check').fetchall() == []
        con.close()
    print('PASS: schema creation, foreign keys, role grants, no demo users, balanced journal approval/posting, unbalanced rejection, financial reversal, stock posting gate, stock reversal, posted-ledger immutability, approval guards, append-only audit log, integrity_check.')


if __name__ == '__main__':
    main()
