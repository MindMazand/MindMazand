# -*- coding: utf-8 -*-
"""G9 acceptance tests: double-entry journal engine and profit & loss reporting."""
from __future__ import annotations

import json
import re
import sqlite3
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[1]
APP = ROOT / "app" / "Mazandasht.Desktop"
DB = APP / "Database"
MIGRATIONS = ["schema_v1.sql", "system_seed.sql", "migration_002.sql", "migration_003.sql",
              "migration_004.sql", "migration_005.sql", "migration_006.sql", "migration_007.sql"]
CONTRACT = ROOT / "docs" / "12_Rules_Contract.json"

_id_counter = [0]


def _id():
    _id_counter[0] += 1
    return format(_id_counter[0], "032x")


def _database():
    connection = sqlite3.connect(":memory:")
    connection.execute("PRAGMA foreign_keys=ON")
    for name in MIGRATIONS:
        connection.executescript((DB / name).read_text(encoding="utf-8"))
    connection.commit()
    return connection


def _add_user(connection, user_id, username, role_code):
    connection.execute(
        "INSERT INTO users(id,username,display_name,password_hash,password_salt,"
        "password_iterations,role_id,is_active) VALUES(?,?,?,?,?,?,"
        "(SELECT id FROM roles WHERE role_code=?),1);",
        (user_id, username, username, b"x", b"y", 100000, role_code))
    connection.commit()


def _seed_party(connection, username="پرتقال خانه"):
    party_id = _id()
    ledger_id = _id()
    account_id = _id()
    connection.execute("INSERT INTO parties(id,display_name) VALUES(?,?)", (party_id, username))
    connection.execute(
        "INSERT INTO ledger_accounts(id,account_code,display_name,account_class,normal_side) "
        "VALUES(?,?,?,?, 'DEBIT')", (ledger_id, "PTY" + username[:3], username, "CONTROL"))
    connection.execute(
        "INSERT INTO party_accounts(id,party_id,ledger_account_id) VALUES(?,?,?)",
        (account_id, party_id, ledger_id))
    connection.commit()
    return account_id, ledger_id


def _seed_product(connection):
    product_id = _id()
    connection.execute("INSERT INTO products(id,product_code,display_name) VALUES(?,?,?)",
                       (product_id, "PRD000001", "پرتقال"))
    connection.commit()
    return product_id


def _document(connection, document_id, document_type, user_id, party_account_id=None,
              requires_journal=1, status="REVIEW", date_iso="2026-11-19"):
    connection.execute(
        "INSERT INTO documents(id,document_type,fiscal_year,document_number,date_iso,status,"
        "requires_journal,requires_stock_posting,party_account_id,created_by) "
        "VALUES(?,?, '2026','',?,?,?,0,?,?)",
        (document_id, document_type, date_iso, status, requires_journal, party_account_id, user_id))
    connection.commit()
    return document_id


def _sql_constants(path: Path) -> dict:
    return {name: body for name, body in re.findall(
        r'Sql(\w+)\s*=\s*@"(.*?)";', path.read_text(encoding="utf-8"), re.S)}


def _bind(sql, values):
    return re.sub(r"@\w+", "?", sql), [values.get(n) for n in re.findall(r"@(\w+)", sql)]


def _run(connection, sql, values):
    positional, ordered = _bind(sql, values)
    connection.execute(positional, ordered)


def _account_id(connection, code):
    return connection.execute(
        "SELECT id FROM ledger_accounts WHERE account_code=?", (code,)).fetchone()[0]


def _insert_entry(connection, document_id, lines, date_iso="2026-11-19", user_id=None):
    """lines: list of (account_code, debit, credit)."""
    constants = _sql_constants(APP / "Services" / "JournalService.cs")
    entry_id = _id()
    _run(connection, constants["EntryInsert"], {
        "id": entry_id, "number": "J-" + entry_id, "doc": document_id,
        "date": date_iso, "description": "آزمون"})
    for index, (code, debit, credit) in enumerate(lines, start=1):
        # A 32-hex value is already a ledger account id (a party ledger account);
        # anything else is a system account code.
        account = code if re.fullmatch(r"[0-9a-f]{32}", code) else _account_id(connection, code)
        _run(connection, constants["LineInsert"], {
            "id": _id(), "entry": entry_id, "line": index, "account": account,
            "debit": debit, "credit": credit, "description": None})
    return entry_id


# ---------------------------------------------------------------------------
# Contract conformance
# ---------------------------------------------------------------------------
def test_accounting_rules_match_the_contract():
    contract = json.loads(CONTRACT.read_text(encoding="utf-8"))
    rules = contract["accounting"]
    assert rules["journal"]["minLines"] == 2
    assert rules["journal"]["mustBalance"] is True
    assert rules["journal"]["postRequiresApprovedDocument"] is True
    assert rules["journal"]["postedLinesImmutable"] is True
    assert rules["profitLoss"]["grossProfit"] == "REVENUE - COGS"
    assert rules["profitLoss"]["operatingProfit"] == "grossProfit - OPERATING_EXPENSES"
    assert rules["profitLoss"]["cogsMethod"] == "FIFO"
    assert contract["tax"]["vatRatePercent"] == 9
    assert contract["tax"]["appliesTo"] == "SALE"
    assert contract["reports"]["onlyPostedData"] is True


def test_journal_engine_implements_the_contract_account_mapping():
    contract = json.loads(CONTRACT.read_text(encoding="utf-8"))
    expected = {rule["documentType"]: (rule["debit"], rule["credit"])
                for rule in contract["accounting"]["documentJournalRules"]}
    source = (APP / "Services" / "JournalService.cs").read_text(encoding="utf-8")
    for document_type in ("PURCHASE", "SALE", "BANK_TXN", "CHEQUE"):
        assert '"' + document_type + '"' in source, document_type
    # The concrete account codes used by the engine for the four supported types.
    for code in ("5001", "2001", "1101", "1003", "4001", "2002"):
        assert '"' + code + '"' in source, code
    # Operating expenses are any EXPENSE account other than COGS (5001); the
    # profit/loss service must not hard-code a subset.
    profit = (APP / "Services" / "ProfitLossService.cs").read_text(encoding="utf-8")
    assert 'AccountCode == "5001"' in profit
    assert expected["PURCHASE"][0] == ["COGS", "PURCHASE_EXPENSES"]
    assert expected["SALE"][1] == ["REVENUE", "TAX_PAYABLE"]


def test_module_services_require_and_build_journal_entries():
    for name in ("PurchaseService", "SalesService", "BankService", "ChequeService"):
        source = (APP / "Services" / (name + ".cs")).read_text(encoding="utf-8")
        assert "JournalService.EnsureEntry(connection, transaction" in source, name
        assert "'REVIEW',1,0," in source, name + " must flag requires_journal"


def test_journal_is_created_unposted_so_review_documents_have_no_effect():
    connection = _database()
    constants = _sql_constants(APP / "Services" / "JournalService.cs")
    manager = _id()
    _add_user(connection, manager, "manager_j1", "MANAGER")
    party_account, party_ledger = _seed_party(connection)
    document_id = _id()
    try:
        _document(connection, document_id, "SALE", manager, party_account)
        entry_id = _insert_entry(connection, document_id, [
            (party_ledger, 91744, 0), ("4001", 0, 84000), ("2002", 0, 7744)])
        connection.commit()
        row = connection.execute(
            "SELECT is_posted FROM journal_entries WHERE id=?", (entry_id,)).fetchone()
        assert row[0] == 0
        # Unposted entries must not move any balance.
        balance = connection.execute(
            "SELECT balance_irr FROM v_party_balance WHERE party_account_id=?",
            (party_account,)).fetchone()[0]
        assert balance == 0
        assert connection.execute(
            "SELECT is_posted FROM journal_entries WHERE document_id=?", (document_id,)
        ).fetchone()[0] == 0
        assert constants  # the shipped SQL was used for the insert
    finally:
        connection.close()


def test_journal_cannot_be_posted_before_approval():
    connection = _database()
    manager = _id()
    _add_user(connection, manager, "manager_j2", "MANAGER")
    party_account, party_ledger = _seed_party(connection)
    document_id = _id()
    try:
        _document(connection, document_id, "SALE", manager, party_account, status="REVIEW")
        entry_id = _insert_entry(connection, document_id, [
            (party_ledger, 1000, 0), ("4001", 0, 1000)])
        connection.commit()
        with pytest.raises(sqlite3.IntegrityError):
            connection.execute(
                "UPDATE journal_entries SET is_posted=1 WHERE id=?", (entry_id,))
    finally:
        connection.close()


def test_journal_requires_two_lines_and_a_zero_balance():
    connection = _database()
    manager = _id()
    _add_user(connection, manager, "manager_j3", "MANAGER")
    party_account, party_ledger = _seed_party(connection)
    document_id = _id()
    try:
        _document(connection, document_id, "SALE", manager, party_account, status="APPROVED")
        single = _insert_entry(connection, document_id, [(party_ledger, 1000, 0)])
        connection.commit()
        with pytest.raises(sqlite3.IntegrityError):
            connection.execute("UPDATE journal_entries SET is_posted=1 WHERE id=?", (single,))
        # journal_entries.document_id is UNIQUE, so the unbalanced entry needs its
        # own document.
        second_id = _id()
        _document(connection, second_id, "SALE", manager, party_account, status="APPROVED")
        unbalanced = _insert_entry(connection, second_id, [
            (party_ledger, 1000, 0), ("4001", 0, 900)])
        connection.commit()
        with pytest.raises(sqlite3.IntegrityError):
            connection.execute("UPDATE journal_entries SET is_posted=1 WHERE id=?", (unbalanced,))
    finally:
        connection.close()


def test_posted_journal_lines_are_immutable():
    connection = _database()
    manager = _id()
    _add_user(connection, manager, "manager_j4", "MANAGER")
    party_account, party_ledger = _seed_party(connection)
    document_id = _id()
    try:
        _document(connection, document_id, "SALE", manager, party_account, status="APPROVED")
        entry_id = _insert_entry(connection, document_id, [
            (party_ledger, 1000, 0), ("4001", 0, 1000)])
        connection.execute("UPDATE journal_entries SET is_posted=1 WHERE id=?", (entry_id,))
        connection.commit()
        line_id = connection.execute(
            "SELECT id FROM journal_lines WHERE journal_entry_id=? AND debit_irr=1000",
            (entry_id,)).fetchone()[0]
        with pytest.raises(sqlite3.IntegrityError):
            connection.execute(
                "UPDATE journal_lines SET debit_irr=2000 WHERE id=?", (line_id,))
        with pytest.raises(sqlite3.IntegrityError):
            connection.execute("DELETE FROM journal_lines WHERE id=?", (line_id,))
        with pytest.raises(sqlite3.IntegrityError):
            connection.execute(
                "INSERT INTO journal_lines(id,journal_entry_id,line_number,ledger_account_id,"
                "debit_irr,credit_irr) VALUES(?,?,?,?,?,?)",
                (_id(), entry_id, 3, _account_id(connection, "4001"), 0, 1))
    finally:
        connection.close()


def test_approval_requires_a_journal_when_the_flag_is_set():
    connection = _database()
    manager = _id()
    _add_user(connection, manager, "manager_j5", "MANAGER")
    document_id = _id()
    try:
        _document(connection, document_id, "PURCHASE", manager, None, requires_journal=1)
        with pytest.raises(sqlite3.IntegrityError):
            connection.execute(
                "UPDATE documents SET status='APPROVED' WHERE id=?", (document_id,))
        # Without the flag the same approval is allowed.
        other = _id()
        _document(connection, other, "PURCHASE", manager, None, requires_journal=0)
        connection.execute("UPDATE documents SET status='APPROVED' WHERE id=?", (other,))
        connection.commit()
    finally:
        connection.close()


# ---------------------------------------------------------------------------
# Profit & loss
# ---------------------------------------------------------------------------
def _post_sale_and_purchase(connection, manager):
    party_account, party_ledger = _seed_party(connection, "خریدار نمونه")
    product = _seed_product(connection)
    sale_id = _id()
    _document(connection, sale_id, "SALE", manager, party_account, status="APPROVED")
    # 100 kg sold at 10000 Rial/kg -> 1,000,000 Rial incl. 9% VAT.
    connection.execute(
        "INSERT INTO sales_invoice_lines(id,document_id,line_number,product_id,quantity_g,"
        "unit_price_irr_per_kg,net_receivable_irr) VALUES(?,?,?,?,?,?,?)",
        (_id(), sale_id, 1, product, 100000, 10000, 1000000))
    _insert_entry(connection, sale_id, [
        (party_ledger, 1000000, 0), ("4001", 0, 917431), ("2002", 0, 82569)])
    connection.execute("UPDATE journal_entries SET is_posted=1 WHERE document_id=?", (sale_id,))

    purchase_id = _id()
    _document(connection, purchase_id, "PURCHASE", manager, party_account, status="APPROVED")
    connection.execute(
        "INSERT INTO purchase_invoice_lines(id,document_id,line_number,product_id,quantity_g,"
        "unit_price_irr_per_kg,net_amount_irr) VALUES(?,?,?,?,?,?,?)",
        (_id(), purchase_id, 1, product, 100000, 6000, 600000))
    _insert_entry(connection, purchase_id, [("5001", 600000, 0), ("2001", 0, 600000)])
    connection.execute(
        "UPDATE journal_entries SET is_posted=1 WHERE document_id=?", (purchase_id,))
    connection.commit()
    return sale_id, purchase_id


def test_profit_loss_reads_only_posted_entries():
    connection = _database()
    constants = _sql_constants(APP / "Services" / "ProfitLossService.cs")
    manager = _id()
    _add_user(connection, manager, "manager_pl", "MANAGER")
    try:
        _post_sale_and_purchase(connection, manager)
        positional, ordered = _bind(constants["Report"], {"from": "2026-11-01", "to": "2026-11-30"})
        rows = connection.execute(positional, ordered).fetchall()
        by_code = {row[0]: row for row in rows}
        revenue = by_code["4001"][4] - by_code["4001"][3]
        cogs = by_code["5001"][3] - by_code["5001"][4]
        assert revenue == 917431
        assert cogs == 600000
        assert revenue - cogs == 317431
        assert by_code["2002"][4] == 82569
    finally:
        connection.close()


def test_profit_loss_ignores_unposted_entries():
    connection = _database()
    constants = _sql_constants(APP / "Services" / "ProfitLossService.cs")
    manager = _id()
    _add_user(connection, manager, "manager_pl2", "MANAGER")
    party_account, party_ledger = _seed_party(connection)
    document_id = _id()
    try:
        _document(connection, document_id, "SALE", manager, party_account, status="REVIEW")
        _insert_entry(connection, document_id, [(party_ledger, 500000, 0), ("4001", 0, 500000)])
        connection.commit()
        positional, ordered = _bind(constants["Report"], {"from": "2026-11-01", "to": "2026-11-30"})
        rows = connection.execute(positional, ordered).fetchall()
        by_code = {row[0]: row for row in rows}
        assert by_code["4001"][4] == 0
        assert by_code["1101"][3] == 0
    finally:
        connection.close()


def test_v_party_balance_reflects_posted_journals():
    connection = _database()
    manager = _id()
    _add_user(connection, manager, "manager_pb", "MANAGER")
    party_account, party_ledger = _seed_party(connection)
    document_id = _id()
    try:
        _document(connection, document_id, "SALE", manager, party_account, status="APPROVED")
        _insert_entry(connection, document_id, [
            (party_ledger, 1000000, 0), ("4001", 0, 917431), ("2002", 0, 82569)])
        connection.execute("UPDATE journal_entries SET is_posted=1 WHERE document_id=?", (document_id,))
        connection.commit()
        row = connection.execute(
            "SELECT total_debit_irr,total_credit_irr,balance_irr FROM v_party_balance "
            "WHERE party_account_id=?", (party_account,)).fetchone()
        assert row[0] == 1000000
        assert row[1] == 0
        assert row[2] == 1000000
    finally:
        connection.close()


# ---------------------------------------------------------------------------
# Wiring
# ---------------------------------------------------------------------------
def test_accounting_window_is_wired_into_the_shell():
    main_window = (APP / "MainWindow.xaml").read_text(encoding="utf-8")
    main_cs = (APP / "MainWindow.xaml.cs").read_text(encoding="utf-8")
    assert main_window.count('Click="OpenAccounting_Click"') == 1
    assert "new AccountingWindow(_databasePath, user.UserId)" in main_cs
    window = (APP / "Views" / "AccountingWindow.cs").read_text(encoding="utf-8")
    for label in ("المجو", "سود و زیان", "حسابداری"):
        assert label in window, label
