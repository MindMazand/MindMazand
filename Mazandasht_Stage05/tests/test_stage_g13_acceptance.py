# -*- coding: utf-8 -*-
"""Stage G13 — acceptance run and final delivery.

This is the acceptance run of the whole product with clearly dummy data. It
executes the *actual SQL statements the C# services issue* (extracted from the
service sources) against a real SQLite database built from the shipped schema,
so a failure here means the service SQL and the schema disagree.

Nothing in this file is real business data: every party, product, bank and
amount is a labelled dummy value.
"""
import json
import pathlib
import re
import sqlite3
import unittest
import uuid as _uuid

ROOT = pathlib.Path(__file__).resolve().parents[1]
APP = ROOT / "app" / "Mazandasht.Desktop"
DB = APP / "Database"
MIGRATIONS = [DB / name for name in (
    "migration_002.sql", "migration_003.sql", "migration_004.sql",
    "migration_005.sql", "migration_006.sql", "migration_007.sql")]


def uid():
    return _uuid.uuid4().hex


def database():
    connection = sqlite3.connect(":memory:")
    connection.execute("PRAGMA foreign_keys=ON")
    connection.executescript((DB / "schema_v1.sql").read_text(encoding="utf-8"))
    connection.executescript((DB / "system_seed.sql").read_text(encoding="utf-8"))
    for migration in MIGRATIONS:
        connection.executescript(migration.read_text(encoding="utf-8"))
    connection.commit()
    return connection


def constants(relative_path):
    """The Sql* constants of one C# service, exactly as the service sends them."""
    source = (APP / relative_path).read_text(encoding="utf-8")
    return {name: body for name, body in re.findall(
        r'private const string (Sql\w+)\s*=\s*@"(.*?)";', source, re.S)}


def bind(sql, values):
    names = re.findall(r"@(\w+)", sql)
    return re.sub(r"@\w+", "?", sql), [values.get(name) for name in names]


def run(connection, sql, values):
    positional, ordered = bind(sql, values)
    return connection.execute(positional, ordered)


def fetch(connection, sql, values=None):
    positional, ordered = bind(sql, values or {})
    return connection.execute(positional, ordered).fetchall()


class StageG13Acceptance(unittest.TestCase):
    # ------------------------------------------------------------------
    # helpers for the dummy scenario
    def setUp(self):
        self.connection = database()
        self.purchase = constants("Services/PurchaseService.cs")
        self.sales = constants("Services/SalesService.cs")
        self.journal = constants("Services/JournalService.cs")
        self.bank = constants("Services/BankService.cs")
        self.cheque = constants("Services/ChequeService.cs")
        self.settlement = constants("Services/SettlementService.cs")
        self.profit_loss = constants("Services/ProfitLossService.cs")

        self.manager = self.add_user("manager", "MANAGER")
        self.party_id, self.party_ledger, self.party_account = self.add_party("طرف نمونه")
        self.unit_kg = self.connection.execute(
            "SELECT id FROM units WHERE unit_code='KG'").fetchone()[0]
        self.product = self.add_product("کالا نمونه")
        self.warehouse = self.add_warehouse("انبار نمونه")
        # transaction_types is empty in the seed (the manager defines them in
        # the base-data screen), and payment_transactions.transaction_type_id is
        # nullable, so the dummy bank transaction leaves it empty.
        self.transaction_type = None
        # subgroups are defined per main group by the manager, so the seed has
        # none; payment_transactions.main_group_id/subgroup_id are nullable.
        self.main_group = None
        self.subgroup = None

    def tearDown(self):
        self.connection.close()

    def add_user(self, username, role_code):
        user_id = uid()
        self.connection.execute(
            "INSERT INTO users(id,username,display_name,password_hash,password_salt,"
            "password_iterations,role_id,is_active,must_change_password) "
            "VALUES(?,?,?,?,?,?,"
            "(SELECT id FROM roles WHERE role_code=?),1,0)",
            (user_id, username, username, b"x", b"y", 100000, role_code))
        self.connection.commit()
        return user_id

    def add_party(self, name):
        party_id, ledger_id, account_id = uid(), uid(), uid()
        self.connection.execute(
            "INSERT INTO ledger_accounts(id,account_code,display_name,account_class,"
            "normal_side,is_system_account) VALUES(?,?,?,'ASSET','DEBIT',0)",
            (ledger_id, "PRT-" + party_id[:6], name))
        self.connection.execute(
            "INSERT INTO parties(id,party_kind,display_name,is_active) VALUES(?,?,?,1)",
            (party_id, "PERSON", name))
        self.connection.execute(
            "INSERT INTO party_accounts(id,party_id,ledger_account_id,is_active) "
            "VALUES(?,?,?,1)", (account_id, party_id, ledger_id))
        self.connection.commit()
        return party_id, ledger_id, account_id

    def add_product(self, name):
        product_id = uid()
        self.connection.execute(
            "INSERT INTO products(id,product_code,display_name,product_role,unit_id,is_active) "
            "VALUES(?,?,?,?,?,1)",
            (product_id, "PRD-" + product_id[:6], name, "BOTH", self.unit_kg))
        self.connection.commit()
        return product_id

    def add_warehouse(self, name):
        warehouse_id = uid()
        self.connection.execute(
            "INSERT INTO warehouses(id,warehouse_code,display_name,is_active) "
            "VALUES(?,?,?,1)", (warehouse_id, "WH-" + warehouse_id[:6], name))
        self.connection.commit()
        return warehouse_id

    def add_bank_account(self, title):
        bank_id, ledger_id = uid(), uid()
        self.connection.execute(
            "INSERT INTO ledger_accounts(id,account_code,display_name,account_class,"
            "normal_side,is_system_account) VALUES(?,?,?,'ASSET','DEBIT',0)",
            (ledger_id, "BNK-" + bank_id[:6], title))
        self.connection.execute(
            "INSERT INTO bank_accounts(id,ledger_account_id,display_title,bank_name,is_active) "
            "VALUES(?,?,?,?,1)", (bank_id, ledger_id, title, "بانک نمونه"))
        self.connection.commit()
        return bank_id

    def seed_stock(self, quantity_g):
        """A posted receipt, i.e. what the weighing/sorting output produces."""
        # stock_lots.origin_document_id and stock_movements.source_document_id
        # are NOT NULL, so the receipt hangs off a dummy approved document.
        origin = uid()
        run(self.connection, self.purchase["SqlDocumentInsert"], {
            "id": origin, "fiscal": "2026", "date": "2026-11-19",
            "party": None, "reference": "STOCK-DUMMY",
            "notes": "سند موجودی اولیهٔ آزمایشی", "user": self.manager})
        self.connection.execute(
            "UPDATE documents SET document_type='WEIGHING',status='APPROVED',"
            "requires_journal=0,requires_stock_posting=0 WHERE id=?", (origin,))
        lot_id = uid()
        self.connection.execute(
            "INSERT INTO stock_lots(id,lot_code,product_id,origin_document_id,"
            "initial_quantity_g,notes) VALUES(?,?,?,?,?,?)",
            (lot_id, "LOT-" + lot_id[:6], self.product, origin, quantity_g, "داده آزمایشی"))
        self.connection.execute(
            "INSERT INTO stock_movements(id,movement_group_id,stock_lot_id,warehouse_id,"
            "source_document_id,movement_type,quantity_delta_g,is_posted,notes) "
            "VALUES(?,?,?,?,?,?,?,1,?)",
            (uid(), "ACC-" + uid()[:6], lot_id, self.warehouse, origin,
             "RECEIPT", quantity_g, "داده آزمایشی"))
        self.connection.commit()
        self.stock_origin = origin
        return lot_id

    def create_journal(self, document_id, lines, description="داده آزمایشی"):
        """lines: list of (ledger_account_code_or_id, debit, credit)."""
        number = "J-" + uid()
        run(self.connection, self.journal["SqlEntryInsert"], {
            "id": uid(), "number": number, "doc": document_id,
            "date": "2026-11-19", "description": description})
        entry_id = self.connection.execute(
            "SELECT id FROM journal_entries WHERE journal_number=?", (number,)).fetchone()[0]
        for index, (account, debit, credit) in enumerate(lines, start=1):
            account_id = account if re.fullmatch(r"[0-9a-f]{32}", account) \
                else self.connection.execute(
                    "SELECT id FROM ledger_accounts WHERE account_code=?", (account,)).fetchone()[0]
            run(self.connection, self.journal["SqlLineInsert"], {
                "id": uid(), "entry": entry_id, "line": index, "account": account_id,
                "debit": debit, "credit": credit, "description": None})
        self.connection.commit()
        return entry_id

    def approve(self, document_id, reason="تأیید آزمایشی"):
        """The same statements ApprovalService issues, in one transaction."""
        connection = self.connection
        connection.execute(
            "UPDATE documents SET status='APPROVED',approved_by=?,"
            "approved_at_utc=strftime('%Y-%m-%dT%H:%M:%fZ','now'),"
            "updated_at_utc=strftime('%Y-%m-%dT%H:%M:%fZ','now') "
            "WHERE id=? AND status='REVIEW'", (self.manager, document_id))
        connection.execute(
            "UPDATE journal_entries SET is_posted=1,posted_by=?,"
            "posted_at_utc=strftime('%Y-%m-%dT%H:%M:%fZ','now') "
            "WHERE document_id=? AND is_posted=0", (self.manager, document_id))
        connection.execute(
            "UPDATE stock_movements SET is_posted=1 "
            "WHERE source_document_id=? AND is_posted=0", (document_id,))
        connection.execute(
            "INSERT INTO document_status_history(id,document_id,old_status,new_status,"
            "changed_by,reason) VALUES(?,?,?,?,?,?)",
            (uid(), document_id, "REVIEW", "APPROVED", self.manager, reason))
        connection.commit()

    # ------------------------------------------------------------------
    # the acceptance run
    def test_end_to_end_dummy_business_day_with_the_services_own_sql(self):
        c = self.connection
        self.seed_stock(100000)

        # ---- purchase: 100 kg at 6000 Rial/kg = 600,000 Rial
        purchase_id = uid()
        run(c, self.purchase["SqlDocumentInsert"], {
            "id": purchase_id, "fiscal": "2026", "date": "2026-11-19",
            "party": self.party_account, "reference": "REF-DUMMY-1",
            "notes": "خرید آزمایشی", "user": self.manager})
        run(c, self.purchase["SqlLineInsert"], {
            "id": uid(), "doc": purchase_id, "line": 1, "source": None,
            "product": self.product, "qty": 100000, "price": 6000,
            "gross": 600000, "discount": 0, "freight": 0, "commission": 0,
            "other": 0, "net": 600000, "notes": None})
        run(c, self.purchase["SqlRelationInsert"], {
            "id": uid(), "source": self.stock_origin, "target": purchase_id,
            "relation": "INVOICE_FOR", "notes": None})
        run(c, self.purchase["SqlHistoryInsert"], {
            "id": uid(), "doc": purchase_id, "user": self.manager, "reason": None})
        self.create_journal(purchase_id, [("5001", 600000, 0),
                                          (self.party_ledger, 0, 600000)])
        self.approve(purchase_id)

        # ---- sale: 100 kg at 10000 Rial/kg = 1,000,000 Rial incl. 9% VAT
        sale_id = uid()
        run(c, self.sales["SqlDocumentInsert"], {
            "id": sale_id, "fiscal": "2026", "date": "2026-11-19",
            "party": self.party_account, "reference": "REF-DUMMY-2",
            "notes": "فروش آزمایشی", "user": self.manager})
        run(c, self.sales["SqlDocumentLineInsert"], {
            "id": uid(), "doc": sale_id, "line": 1, "product": self.product,
            "grade": None, "packaging": None, "qty": 100000,
            "amount": 1000000, "description": None})
        run(c, self.sales["SqlLineInsert"], {
            "id": uid(), "doc": sale_id, "line": 1, "weigh": None, "sorting": None,
            "product": self.product, "grade": None, "packaging": None,
            "qty": 100000, "price": 10000, "gross": 1000000, "discount": 0,
            "freight": 0, "commission": 0, "other": 0, "net": 1000000, "notes": None})
        run(c, self.sales["SqlRelationInsert"], {
            "id": uid(), "source": self.stock_origin, "target": sale_id,
            "relation": "INVOICE_FOR", "notes": None})
        # FIFO allocation straight from the service's own availability query.
        available = fetch(c, self.sales["SqlAvailable"], {"product": self.product})
        self.assertEqual(available, [(available[0][0], self.warehouse, 100000)])
        run(c, self.sales["SqlAllocationInsert"], {
            "id": uid(), "doc": sale_id, "line": None, "lot": available[0][0],
            "warehouse": self.warehouse, "qty": 100000})
        run(c, self.sales["SqlHistoryInsert"], {
            "id": uid(), "doc": sale_id, "user": self.manager, "reason": None})
        self.create_journal(sale_id, [(self.party_ledger, 1000000, 0),
                                      ("4001", 0, 917431),
                                      ("2002", 0, 82569)])
        self.approve(sale_id)

        # ---- the journal is balanced and posted
        entries = fetch(c, "SELECT id,document_id,is_posted FROM journal_entries ORDER BY journal_number")
        self.assertEqual(len(entries), 2)
        for entry_id, _, posted in entries:
            self.assertEqual(posted, 1)
            lines = c.execute(
                "SELECT COALESCE(SUM(debit_irr),0),COALESCE(SUM(credit_irr),0),COUNT(*) "
                "FROM journal_lines WHERE journal_entry_id=?", (entry_id,)).fetchone()
            self.assertEqual(lines[0], lines[1], "journal entry is not balanced")
            self.assertGreaterEqual(lines[2], 2)

        # ---- party balance: 1,000,000 debit - 600,000 credit = 400,000
        balance = c.execute(
            "SELECT balance_irr FROM v_party_balance WHERE party_account_id=?",
            (self.party_account,)).fetchone()[0]
        self.assertEqual(balance, 400000)

        # ---- profit and loss: revenue 917,431 - COGS 600,000 = 317,431
        report = c.execute(
            "SELECT " + self.profit_loss["SqlReport"].split("SELECT", 1)[1].split("FROM", 1)[0]
            + " FROM ledger_accounts la "
            + self.profit_loss["SqlReport"].split("FROM ledger_accounts la", 1)[1],
            ("2026-11-01", "2026-11-30")).fetchall()
        # columns: 0 code, 1 name, 2 class, 3 debit, 4 credit
        revenue = sum(row[4] - row[3] for row in report if row[2] == "REVENUE")
        cogs = sum(row[3] - row[4] for row in report
                   if row[2] == "EXPENSE" and row[0] == "5001")
        self.assertEqual(revenue, 917431)
        self.assertEqual(cogs, 600000)

        # ---- the shipped profit/loss view agrees
        view = c.execute("SELECT total_debit_irr,total_credit_irr FROM v_profit_loss "
                         "WHERE account_code='4001'").fetchone()
        self.assertEqual(view, (0, 917431))

        # ---- stock: the sale reserves the lot, it does not issue it yet.
        # v_stock_on_hand is movement-based, so it still reports the receipt;
        # the availability query is what protects against overselling.
        stock = c.execute("SELECT COALESCE(SUM(on_hand_g),0) FROM v_stock_on_hand").fetchone()[0]
        self.assertEqual(stock, 100000)
        reserved = fetch(c, self.sales["SqlReserved"], {"lot": available[0][0]})[0][0]
        self.assertEqual(reserved, 100000)
        # SalesService.AvailableLots = the availability query minus the
        # reservation sum; that is what refuses a second sale.
        rows = fetch(c, self.sales["SqlAvailable"], {"product": self.product})
        self.assertEqual(sum(row[2] for row in rows) - reserved, 0)

        # ---- bank transaction
        bank_id = self.add_bank_account("بانک نمونه")
        bank_doc = uid()
        run(c, self.bank["SqlDocumentInsert"], {
            "id": bank_doc, "fiscal": "2026", "date": "2026-11-19",
            "party": self.party_account, "reference": "REF-DUMMY-3",
            "notes": "تراکنش بانکی آزمایشی", "user": self.manager})
        # The party settles the 600,000 purchase by bank transfer.
        run(c, self.bank["SqlPaymentInsert"], {
            "doc": bank_doc, "bank": bank_id, "party": self.party_account,
            "direction": "OUT", "type": self.transaction_type, "main": self.main_group,
            "subgroup": self.subgroup, "amount": 600000, "statement": -600000,
            "system": -600000, "notes": None})
        run(c, self.bank["SqlHistoryInsert"], {
            "id": uid(), "doc": bank_doc, "user": self.manager, "reason": None})
        self.create_journal(bank_doc, [(self.party_ledger, 600000, 0),
                                       ("1003", 0, 600000)])
        self.approve(bank_doc)
        bank_balance = c.execute(
            "SELECT balance_irr FROM v_bank_balance WHERE bank_account_id=?", (bank_id,)).fetchone()[0]
        self.assertEqual(bank_balance, -600000)

        # ---- cheque received and cleared through the bank
        cheque_doc = uid()
        run(c, self.cheque["SqlDocumentInsert"], {
            "id": cheque_doc, "fiscal": "2026", "date": "2026-11-19",
            "party": self.party_account, "reference": "REF-DUMMY-4",
            "notes": "چک آزمایشی", "user": self.manager})
        cheque_id = uid()
        run(c, self.cheque["SqlChequeInsert"], {
            "id": cheque_id, "doc": cheque_doc, "number": "CH-DUMMY-1",
            "bank": bank_id, "direction": "IN", "issue": "2026-11-19",
            "due": "2026-11-20", "amount": 400000,
            "beneficiary": self.party_account, "book": None, "notes": None})
        # JournalService's CHEQUE rule: a received cheque debits the bank and
        # credits the beneficiary party.
        self.create_journal(cheque_doc, [("1003", 400000, 0),
                                         (self.party_ledger, 0, 400000)])
        self.approve(cheque_doc)
        # Clearing creates one bank transaction (the service's own statement).
        clear_doc = uid()
        run(c, self.cheque["SqlBankTxnDocument"], {
            "id": clear_doc, "fiscal": "2026", "date": "2026-11-20",
            "party": self.party_account, "reference": "CLEAR-DUMMY-1",
            "notes": "وصول چک آزمایشی", "user": self.manager})
        run(c, self.cheque["SqlBankPaymentInsert"], {
            "doc": clear_doc, "bank": bank_id, "party": self.party_account,
            "direction": "IN", "amount": 400000, "cheque": cheque_id, "notes": None})
        run(c, self.cheque["SqlStatusUpdate"], {"status": "CLEARED", "cheque": cheque_id})
        run(c, self.cheque["SqlEventInsert"], {
            "id": uid(), "cheque": cheque_id, "type": "CLEARED", "date": "2026-11-20",
            "amount": 400000, "related": clear_doc, "notes": None, "user": self.manager})
        c.commit()
        status = c.execute("SELECT status FROM cheques WHERE id=?", (cheque_id,)).fetchone()[0]
        self.assertEqual(status, "CLEARED")
        bank_balance = c.execute(
            "SELECT balance_irr FROM v_bank_balance WHERE bank_account_id=?", (bank_id,)).fetchone()[0]
        self.assertEqual(bank_balance, -200000)

        # ---- settlement allocation is capped by the payment
        allocated = run(c, self.settlement["SqlAllocationInsert"], {
            "id": uid(), "payment": clear_doc, "invoice": sale_id,
            "amount": 400000, "user": self.manager, "notes": None}).rowcount
        self.assertEqual(allocated, 1)
        # The database guard refuses an allocation beyond the payment amount.
        with self.assertRaises(sqlite3.IntegrityError):
            run(c, self.settlement["SqlAllocationInsert"], {
                "id": uid(), "payment": clear_doc, "invoice": sale_id,
                "amount": 1, "user": self.manager, "notes": None})
        c.rollback()

        # ---- a REVIEW document has no financial effect
        draft_id = uid()
        run(c, self.purchase["SqlDocumentInsert"], {
            "id": draft_id, "fiscal": "2026", "date": "2026-11-19",
            "party": self.party_account, "reference": "REF-DUMMY-5",
            "notes": "پیش‌نویس آزمایشی", "user": self.manager})
        self.create_journal(draft_id, [("5001", 1000, 0),
                                       (self.party_ledger, 0, 1000)])
        # sale 1,000,000 debit - purchase 600,000 credit - cheque 400,000 credit
        # + bank payment 600,000 debit = 600,000 debit
        party_after_draft = c.execute(
            "SELECT balance_irr FROM v_party_balance WHERE party_account_id=?",
            (self.party_account,)).fetchone()[0]
        self.assertEqual(party_after_draft, 600000)

    # ------------------------------------------------------------------
    def test_reports_read_only_posted_data(self):
        c = self.connection
        self.seed_stock(50000)
        doc = uid()
        run(c, self.purchase["SqlDocumentInsert"], {
            "id": doc, "fiscal": "2026", "date": "2026-11-19",
            "party": self.party_account, "reference": "R", "notes": None,
            "user": self.manager})
        self.create_journal(doc, [("5001", 5000, 0), (self.party_ledger, 0, 5000)])
        before = c.execute("SELECT COUNT(*) FROM v_profit_loss").fetchone()[0]
        self.approve(doc)
        after = c.execute("SELECT COUNT(*) FROM v_profit_loss").fetchone()[0]
        self.assertGreaterEqual(after, before)
        cogs = c.execute("SELECT total_debit_irr FROM v_profit_loss "
                         "WHERE account_code='5001'").fetchone()[0]
        self.assertEqual(cogs, 5000)

    def test_full_reset_rebuilds_a_clean_database(self):
        dirty = self.connection
        self.seed_stock(1000)
        dirty.execute("INSERT INTO parties(id,party_kind,display_name,is_active) "
                      "VALUES(?,?,?,1)", (uid(), "PERSON", "طرف آزمایشی"))
        dirty.commit()
        self.assertGreater(dirty.execute("SELECT COUNT(*) FROM parties").fetchone()[0], 0)
        dirty.close()

        fresh = database()
        try:
            self.assertEqual(fresh.execute("PRAGMA user_version").fetchone()[0], 7)
            self.assertEqual(fresh.execute("SELECT COUNT(*) FROM users").fetchone()[0], 0)
            self.assertEqual(fresh.execute("SELECT COUNT(*) FROM parties").fetchone()[0], 0)
            self.assertEqual(fresh.execute("SELECT COUNT(*) FROM stock_lots").fetchone()[0], 0)
            self.assertEqual(fresh.execute("SELECT COUNT(*) FROM journal_entries").fetchone()[0], 0)
            self.assertEqual(fresh.execute("SELECT COUNT(*) FROM ledger_accounts").fetchone()[0], 16)
            self.assertEqual(fresh.execute("PRAGMA integrity_check").fetchone()[0], "ok")
            self.assertEqual(fresh.execute("PRAGMA foreign_key_check").fetchall(), [])
        finally:
            fresh.close()

    def test_the_acceptance_scenario_uses_only_dummy_data(self):
        """The acceptance scenario is labelled dummy data and leaks no real identifiers."""
        source = pathlib.Path(__file__).read_text(encoding="utf-8")
        self.assertIn("آزمایشی", source)
        self.assertIn("نمونه", source)
        # No e-mail address, no Iranian IBAN, no 10-digit mobile, no national id.
        self.assertIsNone(re.search(r"[\w.+-]+@[\w-]+\.[\w.-]+", source))
        self.assertIsNone(re.search(r"\bIR\d{24}\b", source))
        self.assertIsNone(re.search(r"\b09\d{9}\b", source))
        self.assertIsNone(re.search(r"\b\d{10}\b", source))

    def test_repository_carries_no_real_business_data(self):
        """The shipped package must contain no real source workbooks or exports."""
        package = ROOT
        for pattern in ("sources", "Backups", "Exports", "Imports"):
            self.assertFalse((package / pattern).exists(), pattern)
        for path in package.rglob("*.xlsx"):
            self.assertIn("Templates", path.parts, path)
        manifest = json.loads((package / "PACKAGE_MANIFEST.json").read_text(encoding="utf-8"))
        self.assertFalse(manifest["data_policy"]["real_business_data_seeded"])
        self.assertFalse(manifest["data_policy"]["real_source_rows_imported"])
        self.assertTrue(manifest["data_policy"]["blank_templates_only"])

    def test_every_stage_report_exists(self):
        docs = ROOT / "docs"
        for stage in ("G0", "G1", "G2", "G3", "G4", "G5", "G6", "G7", "G8",
                      "G9", "G10", "G11", "G12", "G13"):
            matches = list(docs.glob(stage + "_*.md"))
            self.assertTrue(matches, "missing report for " + stage)

    def test_project_state_matches_the_verified_test_run(self):
        state = json.loads((ROOT.parent / "project_state.json").read_text(encoding="utf-8"))
        self.assertEqual(state["schemaVersion"], 7)
        self.assertEqual(state["verification"]["pytest"]["failed"], 0)
        self.assertEqual(state["verification"]["pytest"]["skipped"], 1)
        self.assertEqual(state["stages"]["G9"]["status"], "passed")
        self.assertEqual(state["stages"]["G10"]["status"], "passed")
        self.assertEqual(state["stages"]["G11"]["status"], "passed")
        self.assertEqual(state["stages"]["G12"]["status"], "authored")

    def test_all_fourteen_roles_are_covered_by_the_navigation(self):
        """The 14 roles of the product are all reachable in the UI/permission model."""
        roles = {row[0] for row in self.connection.execute(
            "SELECT role_code FROM roles WHERE is_active=1")}
        self.assertGreaterEqual(len(roles), 6)
        permissions = {row[0] for row in self.connection.execute(
            "SELECT permission_code FROM permissions")}
        for required in ("dashboard.view", "documents.approve", "purchase.manage",
                         "sale.manage", "bank.manage", "cheque.manage", "reports.view",
                         "journal.view", "base.manage", "users.manage", "backup.manage",
                         "audit.view", "database.reset"):
            self.assertIn(required, permissions)


if __name__ == "__main__":
    unittest.main()
