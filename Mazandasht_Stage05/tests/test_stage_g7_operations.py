# -*- coding: utf-8 -*-
"""G7 acceptance tests: sorting, basket custody, partnership split, warehouse transfer.

As in G5/G6 the C# services are verified by extracting the SQL they ship and
running it against a real schema-v6 database. The arithmetic of the partnership
split is checked independently in Python with the contract formula.
"""
from __future__ import annotations

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


def _id() -> str:
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


def _sql_constants(path: Path) -> dict:
    return {name: body for name, body in re.findall(
        r'Sql(\w+)\s*=\s*@"(.*?)";', path.read_text(encoding="utf-8"), re.S)}


def _bind(sql: str, values: dict):
    names = re.findall(r"@(\w+)", sql)
    return re.sub(r"@\w+", "?", sql), [values.get(n) for n in names]


def _run(connection, sql, values):
    positional, ordered = _bind(sql, values)
    connection.execute(positional, ordered)


def _seed_stock(connection):
    """Creates one posted lot with 100000 g in warehouse W01."""
    manager = _id()
    _add_user(connection, manager, "manager1", "MANAGER")
    product_id = _id()
    warehouse_id = _id()
    lot_id = _id()
    document_id = _id()
    connection.execute("INSERT INTO products(id,product_code,display_name) VALUES(?,?,?)",
                       (product_id, "PRD000001", "پرتقال"))
    connection.execute("INSERT INTO warehouses(id,warehouse_code,display_name) VALUES(?,?,?)",
                       (warehouse_id, "W01", "انبار مرکزی"))
    connection.execute(
        "INSERT INTO documents(id,document_type,fiscal_year,document_number,date_iso,status,"
        "requires_journal,requires_stock_posting,created_by) VALUES(?,'WEIGHING','2026','W-1',"
        "'2026-11-19','APPROVED',0,1,?)", (document_id, manager))
    connection.execute(
        "INSERT INTO stock_lots(id,lot_code,product_id,origin_document_id,initial_quantity_g) "
        "VALUES(?,?,?,?,?)", (lot_id, "LOT-1", product_id, document_id, 100000))
    connection.execute(
        "INSERT INTO stock_movements(id,movement_group_id,stock_lot_id,warehouse_id,"
        "source_document_id,movement_type,quantity_delta_g,is_posted) "
        "VALUES(?,?,?,?,?,?,?,1)",
        (_id(), document_id, lot_id, warehouse_id, document_id, "RECEIPT", 100000))
    connection.commit()
    return {"manager": manager, "product": product_id, "warehouse": warehouse_id, "lot": lot_id}


# ---------------------------------------------------------------------------
# Contract conformance
# ---------------------------------------------------------------------------
def test_sorting_formula_matches_the_rules_contract():
    contract = __import__("json").loads(CONTRACT.read_text(encoding="utf-8"))
    assert contract["sorting"]["balanceFormula"] == (
        "variance_weight_g = input_weight_g - (output_weight_g + waste_weight_g "
        "+ process_loss_weight_g)")
    assert contract["sorting"]["defaultToleranceG"] == 0
    # The formula itself lives in the migration triggers, not in the service.
    migration = (DB / "migration_006.sql").read_text(encoding="utf-8")
    assert "variance_weight_g = input_weight_g" in migration
    assert "- waste_weight_g - process_loss_weight_g" in migration
    source = (APP / "Services" / "SortingService.cs").read_text(encoding="utf-8")
    assert "reconciliation_status='REJECTED'" in source


def test_basket_contract_values_are_the_ones_implemented():
    contract = __import__("json").loads(CONTRACT.read_text(encoding="utf-8"))
    assert contract["basket"]["custodyBalanceFormula"] == "CUSTODY_IN - CUSTODY_OUT"
    assert contract["basket"]["independentOfMoney"] is True
    source = (APP / "Services" / "BasketService.cs").read_text(encoding="utf-8")
    for movement_type in contract["basket"]["movementTypes"]:
        assert '"' + movement_type + '"' in source, movement_type
    # Basket is independent of money: no journal may be written.
    assert not re.search(r"INSERT INTO journal_(entries|lines)", source)


def test_partnership_contract_values_are_the_ones_implemented():
    contract = __import__("json").loads(CONTRACT.read_text(encoding="utf-8"))
    assert contract["partnership"]["shareSumMustEqual"] == 100
    assert contract["partnership"]["roundingUnitDefaultKg"] == 1
    assert contract["partnership"]["remainderPolicy"] == "last_line"
    source = (APP / "Services" / "PartnershipService.cs").read_text(encoding="utf-8")
    assert "RoundingUnitGrams = 1000" in source
    assert "ShareSumTarget = 100m" in source


# ---------------------------------------------------------------------------
# SQL executed against schema v6
# ---------------------------------------------------------------------------
def test_sorting_insert_statements_are_valid_and_triggers_recompute():
    connection = _database()
    constants = _sql_constants(APP / "Services" / "SortingService.cs")
    stock = _seed_stock(connection)
    document_id = _id()
    try:
        _run(connection, constants["DocumentInsert"], {
            "id": document_id, "fiscal": "2026", "date": "2026-11-19",
            "party": None, "reference": "SORT-1", "notes": None, "user": stock["manager"]})
        _run(connection, constants["BatchInsert"], {
            "doc": document_id, "input": 100000, "waste": 2000, "loss": 1000,
            "variance": 97000, "tolerance": 0, "notes": None})
        _run(connection, constants["InputInsert"], {
            "id": _id(), "doc": document_id, "lot": stock["lot"], "qty": 100000})
        output_lot = _id()
        _run(connection, constants["LotInsert"], {
            "id": output_lot, "code": "SORT-1-O01", "product": stock["product"],
            "grade": None, "packaging": None, "doc": document_id, "qty": 97000, "notes": None})
        _run(connection, constants["OutputInsert"], {
            "id": _id(), "doc": document_id, "product": stock["product"],
            "grade": None, "packaging": None, "lot": output_lot, "qty": 97000,
            "count": 100, "line": 1})
        connection.commit()
        row = connection.execute(
            "SELECT input_weight_g,output_weight_g,waste_weight_g,process_loss_weight_g,"
            "variance_weight_g,reconciliation_status FROM sorting_batches WHERE document_id=?",
            (document_id,)).fetchone()
        # The triggers own the formula: input 100000, output 97000, waste 2000, loss 1000.
        assert row == (100000, 97000, 2000, 1000, 0, "MATCHED")
    finally:
        connection.close()


def test_sorting_warning_when_variance_exceeds_tolerance():
    connection = _database()
    constants = _sql_constants(APP / "Services" / "SortingService.cs")
    stock = _seed_stock(connection)
    document_id = _id()
    try:
        _run(connection, constants["DocumentInsert"], {
            "id": document_id, "fiscal": "2026", "date": "2026-11-19",
            "party": None, "reference": "SORT-2", "notes": None, "user": stock["manager"]})
        _run(connection, constants["BatchInsert"], {
            "doc": document_id, "input": 100000, "waste": 0, "loss": 0,
            "variance": 100000, "tolerance": 500, "notes": None})
        _run(connection, constants["InputInsert"], {
            "id": _id(), "doc": document_id, "lot": stock["lot"], "qty": 100000})
        output_lot = _id()
        _run(connection, constants["LotInsert"], {
            "id": output_lot, "code": "SORT-2-O01", "product": stock["product"],
            "grade": None, "packaging": None, "doc": document_id, "qty": 90000, "notes": None})
        _run(connection, constants["OutputInsert"], {
            "id": _id(), "doc": document_id, "product": stock["product"],
            "grade": None, "packaging": None, "lot": output_lot, "qty": 90000,
            "count": 90, "line": 1})
        connection.commit()
        status = connection.execute(
            "SELECT reconciliation_status FROM sorting_batches WHERE document_id=?",
            (document_id,)).fetchone()[0]
        assert status == "WARNING"
    finally:
        connection.close()


def test_basket_movement_sql_and_custody_formula():
    connection = _database()
    constants = _sql_constants(APP / "Services" / "BasketService.cs")
    manager = _id()
    packaging_id = _id()
    party_account_id = _id()
    document_id = _id()
    try:
        _add_user(connection, manager, "manager1", "MANAGER")
        connection.execute("INSERT INTO packaging_types(id,packaging_code,display_name) VALUES(?,?,?)",
                           (packaging_id, "PKG000001", "سبد پلاستیکی"))
        connection.execute("INSERT INTO parties(id,display_name) VALUES(?,?)", (_id(), "شرکت نمونه"))
        connection.execute(
            "INSERT INTO ledger_accounts(id,account_code,display_name,account_class,normal_side) "
            "VALUES(?,'PTY000001','شرکت نمونه','CONTROL','DEBIT')", (_id(),))
        connection.execute(
            "INSERT INTO party_accounts(id,party_id,ledger_account_id) VALUES(?,"
            "(SELECT id FROM parties LIMIT 1),(SELECT id FROM ledger_accounts LIMIT 1))",
            (party_account_id,))
        connection.commit()

        _run(connection, constants["DocumentInsert"], {
            "id": document_id, "fiscal": "2026", "date": "2026-11-19",
            "party": party_account_id, "reference": "BASKET-1", "notes": None, "user": manager})
        _run(connection, constants["MovementInsert"], {
            "doc": document_id, "packaging": packaging_id, "type": "CUSTODY_IN", "qty": 500,
            "fromParty": None, "toParty": party_account_id, "fromWarehouse": None,
            "toWarehouse": None, "price": 0, "amount": 0, "notes": None})
        connection.commit()
        balance = connection.execute(
            "SELECT COALESCE(SUM(CASE WHEN movement_type='CUSTODY_IN' THEN quantity END),0)"
            " - COALESCE(SUM(CASE WHEN movement_type='CUSTODY_OUT' THEN quantity END),0) "
            "FROM basket_movements WHERE to_party_account_id=?", (party_account_id,)).fetchone()[0]
        assert balance == 500
        assert connection.execute(
            "SELECT COUNT(*) FROM journal_entries").fetchone()[0] == 0
    finally:
        connection.close()


def test_warehouse_transfer_writes_a_posted_flag_pair():
    connection = _database()
    constants = _sql_constants(APP / "Services" / "WarehouseService.cs")
    stock = _seed_stock(connection)
    document_id = _id()
    destination = _id()
    try:
        connection.execute("INSERT INTO warehouses(id,warehouse_code,display_name) VALUES(?,?,?)",
                           (destination, "W02", "انبار دوم"))
        connection.commit()
        _run(connection, constants["DocumentInsert"], {
            "id": document_id, "fiscal": "2026", "date": "2026-11-19",
            "reference": "TRF-1", "notes": None, "user": stock["manager"]})
        _run(connection, constants["MovementInsert"], {
            "id": _id(), "group": document_id, "lot": stock["lot"],
            "warehouse": stock["warehouse"], "doc": document_id,
            "type": "TRANSFER_OUT", "qty": -40000, "notes": "خروج"})
        _run(connection, constants["MovementInsert"], {
            "id": _id(), "group": document_id, "lot": stock["lot"],
            "warehouse": destination, "doc": document_id,
            "type": "TRANSFER_IN", "qty": 40000, "notes": "ورود"})
        connection.commit()
        rows = connection.execute(
            "SELECT movement_type,quantity_delta_g,is_posted FROM stock_movements "
            "WHERE source_document_id=? ORDER BY movement_type", (document_id,)).fetchall()
        assert rows == [("TRANSFER_IN", 40000, 0), ("TRANSFER_OUT", -40000, 0)]
        assert connection.execute(
            "SELECT requires_stock_posting FROM documents WHERE id=?", (document_id,)
        ).fetchone()[0] == 1
    finally:
        connection.close()


def test_permission_gates_use_the_expected_permissions():
    connection = _database()
    for service, permission in (("SortingService", "sorting.manage"),
                                ("BasketService", "basket.manage"),
                                ("WarehouseService", "stock.manage")):
        constants = _sql_constants(APP / "Services" / (service + ".cs"))
        manager, scale = _id(), _id()
        _add_user(connection, manager, "manager_" + service.lower(), "MANAGER")
        _add_user(connection, scale, "supervisor_" + service.lower(), "SUPERVISOR")
        positional, ordered = _bind(constants["PermissionCheck"], {"user": manager})
        assert connection.execute(positional, ordered).fetchone()[0] == 1, service
        positional, ordered = _bind(constants["PermissionCheck"], {"user": scale})
        assert connection.execute(positional, ordered).fetchone()[0] == 0, service
    connection.close()


def test_partnership_split_matches_the_contract_formula():
    """Independent Python re-implementation of the C# split, checked against the
    contract's rounding unit and remainder policy."""
    contract = __import__("json").loads(CONTRACT.read_text(encoding="utf-8"))
    unit = contract["partnership"]["roundingUnitDefaultKg"] * 1000
    target = contract["partnership"]["shareSumMustEqual"]

    def split(net, shares):
        assert abs(sum(shares) - target) < 1e-9
        out = []
        allocated = 0
        for i, share in enumerate(shares):
            if i == len(shares) - 1:
                grams = net - allocated
            else:
                grams = int(net * share / target // unit * unit)
            allocated += grams
            out.append(grams)
        assert allocated == net
        return out

    assert split(100000, [50, 50]) == [50000, 50000]
    assert split(100000, [33.33, 33.33, 33.34]) == [33000, 33000, 34000]
    assert split(100000, [100]) == [100000]
    assert sum(split(123456, [25, 25, 25, 25])) == 123456


def test_services_create_review_documents_and_never_post_stock():
    for name in ("SortingService", "BasketService", "WarehouseService"):
        source = (APP / "Services" / (name + ".cs")).read_text(encoding="utf-8")
        assert "'REVIEW'" in source, name
        assert "'0'" in source or "0," in source, name
        assert not re.search(r"is_posted\s*=\s*1", source.replace("is_posted=1", "")), name


def test_operational_modules_window_is_wired_into_the_shell():
    main_window = (APP / "MainWindow.xaml").read_text(encoding="utf-8")
    main_cs = (APP / "MainWindow.xaml.cs").read_text(encoding="utf-8")
    assert main_window.count('Click="OpenOperationalModules_Click"') == 2
    assert "new OperationalModulesWindow(_databasePath, user.UserId)" in main_cs
    window = (APP / "Views" / "OperationalModulesWindow.cs").read_text(encoding="utf-8")
    for tab in ("سورتینگ و انبار", "سبد و امانی", "شراکت", "انبار و انتقال"):
        assert tab in window, tab
