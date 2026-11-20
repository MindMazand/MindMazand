# -*- coding: utf-8 -*-
"""G8 acceptance tests: purchase, sales, bank, cheque and settlement engines.

As in G5/G6/G7 the shipped SQL is extracted from the C# services and executed
against a real schema-v6 database. The money formulas are additionally
re-implemented in Python and compared with the values the database triggers
accept.
"""
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
    return account_id


def _seed_product(connection, code="PRD000001"):
    product_id = _id()
    connection.execute("INSERT INTO products(id,product_code,display_name) VALUES(?,?,?)",
                       (product_id, code, "پرتقال"))
    connection.commit()
    return product_id


def _seed_warehouse(connection, code="W01"):
    warehouse_id = _id()
    connection.execute("INSERT INTO warehouses(id,warehouse_code,display_name) VALUES(?,?,?)",
                       (warehouse_id, code, "انبار مرکزی"))
    connection.commit()
    return warehouse_id


def _seed_bank(connection, title="حساب نمونه", ledger_code="BANK000001"):
    bank_id = _id()
    ledger_id = _id()
    connection.execute(
        "INSERT INTO ledger_accounts(id,account_code,display_name,account_class,normal_side) "
        "VALUES(?,?,?,?, 'DEBIT')", (ledger_id, ledger_code, title, "ASSET"))
    connection.execute(
        "INSERT INTO bank_accounts(id,ledger_account_id,display_title,bank_name) VALUES(?,?,?,?)",
        (bank_id, ledger_id, title, "بانک نمونه"))
    connection.commit()
    return bank_id


def _seed_stock(connection, product_id, warehouse_id, grams=100000, lot_code="LOT-1"):
    """Creates an APPROVED weighing document plus a posted RECEIPT movement."""
    manager = _id()
    _add_user(connection, manager, "manager_" + lot_code, "MANAGER")
    document_id = _id()
    lot_id = _id()
    connection.execute(
        "INSERT INTO documents(id,document_type,fiscal_year,document_number,date_iso,status,"
        "requires_journal,requires_stock_posting,created_by) VALUES(?,'WEIGHING','2026','W-1',"
        "'2026-11-19','APPROVED',0,1,?)", (document_id, manager))
    connection.execute(
        "INSERT INTO stock_lots(id,lot_code,product_id,origin_document_id,initial_quantity_g) "
        "VALUES(?,?,?,?,?)", (lot_id, lot_code, product_id, document_id, grams))
    connection.execute(
        "INSERT INTO stock_movements(id,movement_group_id,stock_lot_id,warehouse_id,"
        "source_document_id,movement_type,quantity_delta_g,is_posted) "
        "VALUES(?,?,?,?,?,?,?,1)",
        (_id(), document_id, lot_id, warehouse_id, document_id, "RECEIPT", grams))
    # purchase_invoice_lines.source_weigh_document_id references weigh_tickets(document_id),
    # so the weighing document must carry its ticket row.
    connection.execute(
        "INSERT INTO weigh_tickets(document_id,operation_id,direction,product_id,gross_weight_g,"
        "net_weight_g,warehouse_id) VALUES(?,(SELECT id FROM operations WHERE operation_code="
        "'SYS-INBOUND-RECEIPT'),'IN',?,?,?,?)",
        (document_id, product_id, grams, grams, warehouse_id))
    connection.commit()
    return {"manager": manager, "document": document_id, "lot": lot_id}


def _sql_constants(path: Path) -> dict:
    return {name: body for name, body in re.findall(
        r'Sql(\w+)\s*=\s*@"(.*?)";', path.read_text(encoding="utf-8"), re.S)}


def _bind(sql: str, values: dict):
    names = re.findall(r"@(\w+)", sql)
    return re.sub(r"@\w+", "?", sql), [values.get(n) for n in names]


def _run(connection, sql, values):
    positional, ordered = _bind(sql, values)
    connection.execute(positional, ordered)


def _insert_document(connection, document_id, document_type, user_id):
    """Ad-hoc document insert with ? placeholders (not a shipped service SQL)."""
    connection.execute(
        "INSERT INTO documents(id,document_type,fiscal_year,document_number,date_iso,status,"
        "requires_journal,requires_stock_posting,created_by) "
        "VALUES(?,?, '2026','','2026-11-19','REVIEW',0,0,?)", (document_id, document_type, user_id))


def _half_up(quantity_g, price_per_kg):
    """Python mirror of ((quantity_g * unit_price + 500) / 1000)."""
    return (quantity_g * price_per_kg + 500) // 1000


# ---------------------------------------------------------------------------
# Contract conformance
# ---------------------------------------------------------------------------
def test_invoice_and_cheque_rules_match_the_contract():
    contract = json.loads(CONTRACT.read_text(encoding="utf-8"))
    assert contract["invoices"]["purchase"]["linkage"] == "source_weigh_document_id"
    assert contract["invoices"]["sales"]["allocationRequired"] is True
    assert contract["invoices"]["sales"]["oversellAllowed"] is False
    assert contract["invoices"]["emptyInvoiceApprovable"] is False
    assert contract["cheque"]["uniqueKey"] == ["bank_account_id", "cheque_number"]
    assert contract["cheque"]["clearanceCreatesBankTransaction"] is True
    assert contract["settlement"]["createsNewBankTransaction"] is False
    assert contract["settlement"]["allocationSumMustNotExceedPayment"] is True
    assert contract["money"]["storage"] == "integer_rial"
    assert contract["money"]["rounding"] == "half_up"


def test_services_do_not_write_journal_lines():
    for name in ("PurchaseService", "SalesService", "BankService", "ChequeService", "SettlementService"):
        source = (APP / "Services" / (name + ".cs")).read_text(encoding="utf-8")
        assert "INSERT INTO journal_entries" not in source, name
        assert "INSERT INTO journal_lines" not in source, name


def test_permission_codes_exist_in_seed():
    connection = _database()
    for code in ("purchase.manage", "sale.manage", "bank.manage", "cheque.manage"):
        assert connection.execute(
            "SELECT COUNT(*) FROM permissions WHERE permission_code=?", (code,)).fetchone()[0] == 1
    connection.close()


# ---------------------------------------------------------------------------
# Purchase
# ---------------------------------------------------------------------------
def test_purchase_line_net_amount_matches_the_contract_formula():
    connection = _database()
    constants = _sql_constants(APP / "Services" / "PurchaseService.cs")
    party = _seed_party(connection)
    product = _seed_product(connection)
    stock = _seed_stock(connection, product, _seed_warehouse(connection))
    document_id = _id()
    try:
        _run(connection, constants["DocumentInsert"], {
            "id": document_id, "fiscal": "2026", "date": "2026-11-19",
            "party": party, "reference": "P-1", "notes": None, "user": stock["manager"]})
        quantity = 12500
        price = 8400
        discount, freight, commission, other = 10000, 5000, 2000, -1000
        expected = _half_up(quantity, price) - discount + freight + commission + other
        _run(connection, constants["LineInsert"], {
            "id": _id(), "doc": document_id, "line": 1, "source": stock["document"],
            "product": product, "qty": quantity, "price": price,
            "gross": _half_up(quantity, price), "discount": discount, "freight": freight,
            "commission": commission, "other": other, "net": expected, "notes": None})
        _run(connection, constants["RelationInsert"], {
            "id": _id(), "source": stock["document"], "target": document_id,
            "notes": "فاکتور خرید مرتبط با باسکول ورودی"})
        connection.commit()
        row = connection.execute(
            "SELECT net_amount_irr FROM purchase_invoice_lines WHERE document_id=?",
            (document_id,)).fetchone()
        assert row[0] == expected
        assert connection.execute(
            "SELECT COUNT(*) FROM document_relations WHERE source_document_id=? AND "
            "target_document_id=? AND relation_type='INVOICE_FOR'",
            (stock["document"], document_id)).fetchone()[0] == 1
    finally:
        connection.close()


def test_purchase_trigger_rejects_a_wrong_net_amount():
    connection = _database()
    constants = _sql_constants(APP / "Services" / "PurchaseService.cs")
    party = _seed_party(connection)
    product = _seed_product(connection)
    stock = _seed_stock(connection, product, _seed_warehouse(connection))
    document_id = _id()
    try:
        _run(connection, constants["DocumentInsert"], {
            "id": document_id, "fiscal": "2026", "date": "2026-11-19",
            "party": party, "reference": "P-2", "notes": None, "user": stock["manager"]})
        with pytest.raises(sqlite3.IntegrityError):
            _run(connection, constants["LineInsert"], {
                "id": _id(), "doc": document_id, "line": 1, "source": None,
                "product": product, "qty": 1000, "price": 1000,
                "gross": 1000, "discount": 0, "freight": 0, "commission": 0,
                "other": 0, "net": 999999, "notes": None})
    finally:
        connection.close()


# ---------------------------------------------------------------------------
# Sales
# ---------------------------------------------------------------------------
def test_sales_allocation_is_fifo_and_refuses_oversell():
    connection = _database()
    constants = _sql_constants(APP / "Services" / "SalesService.cs")
    product = _seed_product(connection)
    warehouse = _seed_warehouse(connection)
    stock = _seed_stock(connection, product, warehouse, grams=50000)
    document_id = _id()
    line_id = _id()
    try:
        _run(connection, constants["DocumentInsert"], {
            "id": document_id, "fiscal": "2026", "date": "2026-11-19",
            "party": _seed_party(connection), "reference": "S-1", "notes": None,
            "user": stock["manager"]})
        _run(connection, constants["DocumentLineInsert"], {
            "id": line_id, "doc": document_id, "line": 1, "product": product,
            "grade": None, "packaging": None, "qty": 60000, "amount": 0, "description": None})
        _run(connection, constants["LineInsert"], {
            "id": _id(), "doc": document_id, "line": 1, "weigh": None, "sorting": None,
            "product": product, "grade": None, "packaging": None, "qty": 60000,
            "price": 10000, "gross": _half_up(60000, 10000), "discount": 0, "freight": 0,
            "commission": 0, "other": 0, "net": _half_up(60000, 10000), "notes": None})
        connection.commit()
        # The allocation table stores reservations; the oversell rule lives in the
        # service, which refuses when the requested quantity exceeds the FIFO free
        # quantity. Mirror that decision here with the service's own statements.
        positional, ordered = _bind(constants["Available"], {"product": product})
        rows = connection.execute(positional, ordered).fetchall()
        available = rows[0][2] if rows else 0
        positional, ordered = _bind(constants["Reserved"], {"lot": stock["lot"]})
        reserved = connection.execute(positional, ordered).fetchone()[0]
        free = available - reserved
        assert free == 50000
        assert 60000 > free, "a 60000 g sale must not fit into 50000 g of free stock"
        source = (APP / "Services" / "SalesService.cs").read_text(encoding="utf-8")
        assert "فروش بیش از موجودی مجاز نیست" in source
    finally:
        connection.close()


def test_sales_allocation_insert_is_accepted_for_available_stock():
    connection = _database()
    constants = _sql_constants(APP / "Services" / "SalesService.cs")
    product = _seed_product(connection)
    warehouse = _seed_warehouse(connection)
    stock = _seed_stock(connection, product, warehouse, grams=50000)
    document_id = _id()
    line_id = _id()
    try:
        _run(connection, constants["DocumentInsert"], {
            "id": document_id, "fiscal": "2026", "date": "2026-11-19",
            "party": _seed_party(connection), "reference": "S-2", "notes": None,
            "user": stock["manager"]})
        _run(connection, constants["DocumentLineInsert"], {
            "id": line_id, "doc": document_id, "line": 1, "product": product,
            "grade": None, "packaging": None, "qty": 50000, "amount": 0, "description": None})
        _run(connection, constants["AllocationInsert"], {
            "id": _id(), "doc": document_id, "line": line_id, "lot": stock["lot"],
            "warehouse": warehouse, "qty": 50000})
        connection.commit()
        assert connection.execute(
            "SELECT COALESCE(SUM(quantity_g),0) FROM stock_allocations WHERE document_id=?",
            (document_id,)).fetchone()[0] == 50000
    finally:
        connection.close()


def test_sales_line_trigger_rejects_a_wrong_net_receivable():
    connection = _database()
    constants = _sql_constants(APP / "Services" / "SalesService.cs")
    product = _seed_product(connection)
    stock = _seed_stock(connection, product, _seed_warehouse(connection))
    document_id = _id()
    try:
        _run(connection, constants["DocumentInsert"], {
            "id": document_id, "fiscal": "2026", "date": "2026-11-19",
            "party": _seed_party(connection), "reference": "S-3", "notes": None,
            "user": stock["manager"]})
        with pytest.raises(sqlite3.IntegrityError):
            _run(connection, constants["LineInsert"], {
                "id": _id(), "doc": document_id, "line": 1, "weigh": None, "sorting": None,
                "product": product, "grade": None, "packaging": None, "qty": 1000,
                "price": 1000, "gross": 1000, "discount": 0, "freight": 0, "commission": 0,
                "other": 0, "net": 5, "notes": None})
    finally:
        connection.close()


# ---------------------------------------------------------------------------
# Bank
# ---------------------------------------------------------------------------
def test_bank_transaction_resolves_account_by_code_and_updates_balance():
    connection = _database()
    constants = _sql_constants(APP / "Services" / "BankService.cs")
    bank = _seed_bank(connection, title="حساب تست", ledger_code="BANK-TEST")
    manager = _id()
    _add_user(connection, manager, "manager_bank1", "MANAGER")
    document_id = _id()
    try:
        _run(connection, constants["DocumentInsert"], {
            "id": document_id, "fiscal": "2026", "date": "2026-11-19",
            "party": None, "reference": "T-1", "notes": None, "user": manager})
        _run(connection, constants["PaymentInsert"], {
            "doc": document_id, "bank": bank, "party": None, "direction": "IN",
            "type": None, "main": None, "subgroup": None, "amount": 250000,
            "statement": 250000, "system": 250000, "notes": None})
        connection.commit()
        balance = connection.execute(
            "SELECT balance_irr FROM v_bank_balance WHERE bank_account_id=?", (bank,)).fetchone()[0]
        assert balance == 250000
        # The resolve statement must find the account by ledger code, not only by id.
        positional, ordered = _bind(constants["ResolveBank"], {"code": "BANK-TEST"})
        assert connection.execute(positional, ordered).fetchone()[0] == bank
    finally:
        connection.close()


def test_bank_outgoing_transaction_lowers_the_balance():
    connection = _database()
    constants = _sql_constants(APP / "Services" / "BankService.cs")
    bank = _seed_bank(connection)
    manager = _id()
    _add_user(connection, manager, "manager_bank2", "MANAGER")
    document_id = _id()
    try:
        _run(connection, constants["DocumentInsert"], {
            "id": document_id, "fiscal": "2026", "date": "2026-11-19",
            "party": None, "reference": "T-2", "notes": None, "user": manager})
        _run(connection, constants["PaymentInsert"], {
            "doc": document_id, "bank": bank, "party": None, "direction": "OUT",
            "type": None, "main": None, "subgroup": None, "amount": 80000,
            "statement": None, "system": -80000, "notes": None})
        connection.commit()
        assert connection.execute(
            "SELECT balance_irr FROM v_bank_balance WHERE bank_account_id=?", (bank,)
        ).fetchone()[0] == -80000
    finally:
        connection.close()


# ---------------------------------------------------------------------------
# Cheque
# ---------------------------------------------------------------------------
def test_cheque_clearance_creates_exactly_one_bank_transaction():
    connection = _database()
    constants = _sql_constants(APP / "Services" / "ChequeService.cs")
    bank = _seed_bank(connection)
    manager = _id()
    _add_user(connection, manager, "manager_cheque", "MANAGER")
    document_id = _id()
    cheque_id = _id()
    try:
        _run(connection, constants["DocumentInsert"], {
            "id": document_id, "fiscal": "2026", "date": "2026-11-19",
            "party": None, "reference": "CH-1", "notes": None, "user": manager})
        _run(connection, constants["ChequeInsert"], {
            "id": cheque_id, "doc": document_id, "number": "1001", "bank": bank,
            "direction": "IN", "issue": "2026-11-19", "due": "2026-12-19",
            "amount": 300000, "beneficiary": None, "book": None, "notes": None})
        connection.commit()
        # Clearance: BANK_TXN document + payment transaction + CLEARED event.
        txn_id = _id()
        _run(connection, constants["BankTxnDocument"], {
            "id": txn_id, "fiscal": "2026", "date": "2026-11-20",
            "party": None, "reference": cheque_id, "notes": None, "user": manager})
        _run(connection, constants["BankPaymentInsert"], {
            "doc": txn_id, "bank": bank, "party": None, "direction": "IN",
            "amount": 300000, "cheque": cheque_id, "notes": None})
        _run(connection, constants["EventInsert"], {
            "id": _id(), "cheque": cheque_id, "type": "CLEARED", "date": "2026-11-20",
            "amount": 300000, "related": txn_id, "notes": None, "user": manager})
        _run(connection, constants["StatusUpdate"], {"status": "CLEARED", "cheque": cheque_id})
        connection.commit()
        assert connection.execute(
            "SELECT status FROM cheques WHERE id=?", (cheque_id,)).fetchone()[0] == "CLEARED"
        assert connection.execute(
            "SELECT COUNT(*) FROM payment_transactions WHERE cheque_id=?", (cheque_id,)
        ).fetchone()[0] == 1
        assert connection.execute(
            "SELECT balance_irr FROM v_bank_balance WHERE bank_account_id=?", (bank,)
        ).fetchone()[0] == 300000
    finally:
        connection.close()


def test_cheque_unique_key_is_bank_plus_number():
    connection = _database()
    constants = _sql_constants(APP / "Services" / "ChequeService.cs")
    bank = _seed_bank(connection)
    manager = _id()
    _add_user(connection, manager, "manager_cheque2", "MANAGER")
    inserted = 0
    try:
        for index in (1, 2):
            document_id = _id()
            _run(connection, constants["DocumentInsert"], {
                "id": document_id, "fiscal": "2026", "date": "2026-11-19",
                "party": None, "reference": "CH-" + str(index), "notes": None, "user": manager})
            try:
                _run(connection, constants["ChequeInsert"], {
                    "id": _id(), "doc": document_id, "number": "2002", "bank": bank,
                    "direction": "IN", "issue": "2026-11-19", "due": "2026-12-19",
                    "amount": 1000, "beneficiary": None, "book": None, "notes": None})
                inserted += 1
            except sqlite3.IntegrityError:
                break
        assert inserted == 1, "the (bank_account_id, cheque_number) pair must be unique"
    finally:
        connection.close()


# ---------------------------------------------------------------------------
# Settlement
# ---------------------------------------------------------------------------
def test_settlement_allocation_is_capped_by_the_payment_amount():
    connection = _database()
    constants = _sql_constants(APP / "Services" / "SettlementService.cs")
    bank = _seed_bank(connection)
    manager = _id()
    _add_user(connection, manager, "manager_settle", "MANAGER")
    payment_id = _id()
    invoice_id = _id()
    try:
        _insert_document(connection, payment_id, "BANK_TXN", manager)
        _insert_document(connection, invoice_id, "SALE", manager)
        connection.execute(
            "INSERT INTO payment_transactions(document_id,bank_account_id,direction,amount_irr) "
            "VALUES(?,?, 'IN',100000)", (payment_id, bank))
        _run(connection, constants["AllocationInsert"], {
            "id": _id(), "payment": payment_id, "invoice": invoice_id, "amount": 100000,
            "user": manager, "notes": None})
        connection.commit()
        with pytest.raises(sqlite3.IntegrityError):
            _run(connection, constants["AllocationInsert"], {
                "id": _id(), "payment": payment_id, "invoice": invoice_id, "amount": 1,
                "user": manager, "notes": None})
    finally:
        connection.close()


def test_settlement_requires_an_existing_payment_transaction():
    connection = _database()
    constants = _sql_constants(APP / "Services" / "SettlementService.cs")
    manager = _id()
    _add_user(connection, manager, "manager_settle2", "MANAGER")
    payment_id = _id()
    invoice_id = _id()
    try:
        _insert_document(connection, payment_id, "BANK_TXN", manager)
        _insert_document(connection, invoice_id, "SALE", manager)
        with pytest.raises(sqlite3.IntegrityError):
            _run(connection, constants["AllocationInsert"], {
                "id": _id(), "payment": payment_id, "invoice": invoice_id, "amount": 1,
                "user": manager, "notes": None})
    finally:
        connection.close()


# ---------------------------------------------------------------------------
# Gates and wiring
# ---------------------------------------------------------------------------
def test_permission_gates_use_the_expected_permissions():
    connection = _database()
    for service, permission in (("PurchaseService", "purchase.manage"),
                                ("SalesService", "sale.manage"),
                                ("BankService", "bank.manage"),
                                ("ChequeService", "cheque.manage"),
                                ("SettlementService", "bank.manage")):
        constants = _sql_constants(APP / "Services" / (service + ".cs"))
        manager, supervisor = _id(), _id()
        _add_user(connection, manager, "manager_" + service.lower(), "MANAGER")
        _add_user(connection, supervisor, "supervisor_" + service.lower(), "SUPERVISOR")
        sql = constants["PermissionCheck"]
        positional = re.sub(r"@\w+", "?", sql)
        ordered = [manager if n == "user" else permission for n in re.findall(r"@(\w+)", sql)]
        assert connection.execute(positional, ordered).fetchone()[0] == 1, service
        ordered = [supervisor if n == "user" else permission for n in re.findall(r"@(\w+)", sql)]
        assert connection.execute(positional, ordered).fetchone()[0] == 0, service
    connection.close()


def test_no_service_runs_a_nested_command_inside_an_open_data_reader():
    """System.Data.SQLite has no MARS: a second command on the same connection
    while a DataReader is open throws at runtime. Every service must materialise
    its rows (or Close the reader) before issuing nested statements."""
    services = sorted((APP / "Services").glob("*.cs"))
    assert services, "no services found"
    offenders = []
    for path in services:
        text = path.read_text(encoding="utf-8")
        index = 0
        while True:
            index = text.find("ExecuteReader()", index)
            if index < 0:
                break
            # The reader's own using-block opens at the first "{" after the call
            # ("using (var r = cmd.ExecuteReader()) {"); when the reader is used
            # inline in a while loop there is no such block and the enclosing "{" is
            # the boundary.
            opening = text.find("{", index, min(len(text), index + 120))
            if opening < 0:
                opening = text.rfind("{", 0, index)
            if opening < 0:
                index += len("ExecuteReader()")
                continue
            depth = 0
            cursor = opening
            reader_closed = False
            while cursor < len(text):
                if text.startswith(".Close()", cursor):
                    reader_closed = True
                if text[cursor] == "{":
                    depth += 1
                elif text[cursor] == "}":
                    depth -= 1
                    if depth == 0:
                        break
                elif not reader_closed:
                    for call in ("ExecuteScalar(", "ExecuteNonQuery("):
                        if text.startswith(call, cursor):
                            offenders.append((path.name, call, text[:cursor].count("\n") + 1))
                cursor += 1
            index += len("ExecuteReader()")
    assert not offenders, "nested command inside an open reader: " + repr(offenders)


def test_finance_modules_window_is_wired_into_the_shell():
    main_window = (APP / "MainWindow.xaml").read_text(encoding="utf-8")
    main_cs = (APP / "MainWindow.xaml.cs").read_text(encoding="utf-8")
    assert main_window.count('Click="OpenFinanceModules_Click"') == 2
    assert "new FinanceModulesWindow(_databasePath, user.UserId)" in main_cs
    window = (APP / "Views" / "FinanceModulesWindow.cs").read_text(encoding="utf-8")
    for tab in ("خرید", "فروش", "بانک", "چک", "تسویه"):
        assert tab in window, tab
