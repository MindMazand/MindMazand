# -*- coding: utf-8 -*-
"""G5 acceptance tests: base-data catalogs.

The C# service cannot be compiled in this Linux environment, so these tests
verify the shipped artefact at the level that actually matters for data
integrity: they extract the exact SQL statements and audit SQL written in
Services/BaseDataService.cs and execute them against a real schema-v6 SQLite
database with foreign keys and triggers enabled.
"""
from __future__ import annotations

import re
import sqlite3
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[1]
APP = ROOT / "app" / "Mazandasht.Desktop"
SERVICE = APP / "Services" / "BaseDataService.cs"
MIGRATIONS = ["schema_v1.sql", "system_seed.sql", "migration_002.sql", "migration_003.sql",
              "migration_004.sql", "migration_005.sql", "migration_006.sql", "migration_007.sql"]

MANAGER_SQL = """
SELECT COUNT(*) FROM users u
JOIN roles r ON r.id=u.role_id
JOIN role_permissions rp ON rp.role_id=r.id
WHERE u.id=@user AND u.is_active=1 AND r.is_active=1
  AND rp.permission_code='base.manage' AND rp.granted=1;
"""


def _database():
    connection = sqlite3.connect(":memory:")
    connection.execute("PRAGMA foreign_keys=ON")
    for name in MIGRATIONS:
        connection.executescript((APP / "Database" / name).read_text(encoding="utf-8"))
    connection.commit()
    return connection


def _add_user(connection, user_id, username, role_code):
    connection.execute(
        "INSERT INTO users(id,username,display_name,password_hash,password_salt,"
        "password_iterations,role_id,is_active) "
        "VALUES(?,?,?,?,?,?,"
        "(SELECT id FROM roles WHERE role_code=?),1);",
        (user_id, username, username, b"x", b"y", 100000, role_code))
    connection.commit()


def _sql_constants(source: str) -> dict[str, str]:
    return {name: body for name, body in re.findall(
        r'Sql(\w+)\s*=\s*@"(.*?)";', source, re.S)}


def _constants() -> dict[str, str]:
    return _sql_constants(SERVICE.read_text(encoding="utf-8"))


def _bind(sql: str, values: dict[str, object]):
    """Turn @name parameters into positional placeholders, preserving order."""
    names = re.findall(r"@(\w+)", sql)
    positional = re.sub(r"@\w+", "?", sql)
    ordered = []
    for name in names:
        value = values[name]
        ordered.append(None if value is None else value)
    return positional, ordered


def _run(connection, sql: str, values: dict[str, object]):
    positional, ordered = _bind(sql, values)
    connection.execute(positional, ordered)


_id_counter = [0]


def _id() -> str:
    _id_counter[0] += 1
    return format(_id_counter[0], "032x")


# ---------------------------------------------------------------------------
# Source invariants
# ---------------------------------------------------------------------------
def test_service_never_hard_deletes_reference_data():
    source = SERVICE.read_text(encoding="utf-8")
    assert not re.search(r"\bDELETE\s+FROM\b", source, re.I), \
        "base-data reference records must be deactivated, never deleted"
    assert "is_active" in source


def test_every_write_path_is_permission_checked_and_audited():
    source = SERVICE.read_text(encoding="utf-8")
    # Every public mutating method must both require the permission and write audit.
    methods = re.findall(
        r"public static (?:void|string) (Create\w+|Set\w+|Rename\w+)\(", source)
    assert len(methods) >= 12, methods
    bodies = re.split(r"\n        public static (?:void|string) (?:Create\w+|Set\w+|Rename\w+)\(", source)[1:]
    assert len(bodies) == len(methods)
    for name, body in zip(methods, bodies):
        method_body = body.split("\n        public static ")[0]
        assert "RequireBaseManager" in method_body, \
            name + " writes without the base.manage permission check"
        assert "WriteAudit(" in method_body, name + " writes without an audit row"
        assert "BeginTransaction" in method_body, name + " is not transactional"


def test_service_uses_only_named_sql_constants_for_catalog_writes():
    source = SERVICE.read_text(encoding="utf-8")
    constants = _constants()
    expected = {"MainGroupInsert", "MainGroupUpdate", "SubGroupInsert", "TransactionTypeInsert",
                "ProductGradeInsert", "ProductPackagingLink", "ProductPackagingDefault",
                "WorkGroupInsert", "EmployeeInsert", "ParameterUpsert", "AccountNatureInsert",
                "AccountSubNatureInsert", "AccountingGroupInsert", "AuditGroupInsert",
                "BankAccountInsert"}
    missing = expected - set(constants)
    assert not missing, f"missing SQL constants: {sorted(missing)}"


# ---------------------------------------------------------------------------
# Behavioural tests against a real schema-v6 database
# ---------------------------------------------------------------------------
def test_every_sql_constant_placeholder_has_a_bound_parameter():
    source = SERVICE.read_text(encoding="utf-8")
    bound = set(re.findall(r'AddWithValue\("(@?\w+)"', source))
    for name, body in _constants().items():
        for placeholder in set(re.findall(r"@(\w+)", body)):
            assert "@" + placeholder in bound or placeholder in bound, \
                f"{name} uses @{placeholder} but never binds it"


def test_main_group_insert_and_rename_keep_the_reference_code():
    connection = _database()
    constants = _constants()
    try:
        group_id = _id()
        _run(connection, constants["MainGroupInsert"],
             {"id": group_id, "code": "FRUIT", "name": "میوه", "notes": None})
        connection.commit()
        before = connection.execute(
            "SELECT group_code, display_name FROM main_groups WHERE id=?", (group_id,)).fetchone()
        assert before == ("FRUIT", "میوه")

        _run(connection, constants["MainGroupUpdate"],
             {"id": group_id, "code": "FRUIT", "name": "میوه و سبزیجات", "notes": "به‌روز"})
        connection.commit()
        after = connection.execute(
            "SELECT group_code, display_name FROM main_groups WHERE id=?", (group_id,)).fetchone()
        # The reference code is immutable; only the display name changes.
        assert after == ("FRUIT", "میوه و سبزیجات")
    finally:
        connection.close()


def test_subgroup_insert_resolves_a_ledger_account_code():
    connection = _database()
    constants = _constants()
    try:
        main_group_id = _id()
        _run(connection, constants["MainGroupInsert"],
             {"id": main_group_id, "code": "FRUIT", "name": "میوه", "notes": None})
        connection.commit()
        ledger_id = connection.execute(
            "SELECT id FROM ledger_accounts WHERE account_code='1001'").fetchone()[0]
        resolve_sql = ("SELECT id FROM ledger_accounts WHERE account_code=@code "
                       "AND is_active=1;")
        positional, ordered = _bind(resolve_sql, {"code": "1001"})
        assert connection.execute(positional, ordered).fetchone()[0] == ledger_id

        subgroup_id = _id()
        _run(connection, constants["SubGroupInsert"],
             {"id": subgroup_id, "code": "CITRUS", "mainGroup": main_group_id,
              "name": "مرکبات", "ledger": ledger_id, "notes": None})
        connection.commit()
        row = connection.execute(
            "SELECT subgroup_code, main_group_id, ledger_account_id FROM subgroups WHERE id=?",
            (subgroup_id,)).fetchone()
        assert row == ("CITRUS", main_group_id, ledger_id)
    finally:
        connection.close()


def test_transaction_type_effect_check_is_enforced_by_the_schema():
    connection = _database()
    constants = _constants()
    try:
        with pytest.raises(sqlite3.IntegrityError):
            _run(connection, constants["TransactionTypeInsert"],
                 {"id": _id(), "code": "BAD", "name": "نامعتبر", "effect": "BOGUS",
                  "notes": None})
        connection.rollback()
        _run(connection, constants["TransactionTypeInsert"],
             {"id": _id(), "code": "DEP", "name": "واریز", "effect": "INCREASE", "notes": None})
        connection.commit()
        assert connection.execute(
            "SELECT COUNT(*) FROM transaction_types WHERE type_code='DEP'").fetchone()[0] == 1
    finally:
        connection.close()


def test_product_grade_and_packaging_default_round_trip():
    connection = _database()
    constants = _constants()
    try:
        product_id = _id()
        connection.execute(
            "INSERT INTO products(id,product_code,display_name,unit_id) VALUES(?,?,?,"
            "(SELECT id FROM units WHERE unit_code='KG'))",
            (product_id, "PRD000001", "پرتقال"))
        packaging_a, packaging_b = _id(), "b" * 32
        for pid, code in ((packaging_a, "PKG000001"), (packaging_b, "PKG000002")):
            connection.execute(
                "INSERT INTO packaging_types(id,packaging_code,display_name) VALUES(?,?,?)",
                (pid, code, code))
        connection.commit()

        _run(connection, constants["ProductGradeInsert"],
             {"id": _id(), "product": product_id, "code": "GRADE1",
              "name": "درجه یک", "sort": 1})
        connection.commit()
        assert connection.execute(
            "SELECT COUNT(*) FROM product_grades WHERE grade_code='GRADE1'").fetchone()[0] == 1

        _run(connection, constants["ProductPackagingLink"],
             {"product": product_id, "packaging": packaging_a, "default": 1})
        _run(connection, constants["ProductPackagingDefault"],
             {"product": product_id, "packaging": packaging_b})
        # The link must exist before the default flag can be reassigned.
        _run(connection, constants["ProductPackagingLink"],
             {"product": product_id, "packaging": packaging_b, "default": 0})
        _run(connection, constants["ProductPackagingDefault"],
             {"product": product_id, "packaging": packaging_b})
        connection.commit()
        rows = connection.execute(
            "SELECT packaging_type_id,is_default FROM product_packaging WHERE product_id=?",
            (product_id,)).fetchall()
        defaults = [row for row in rows if row[1] == 1]
        assert len(defaults) == 1 and defaults[0][0] == packaging_b
    finally:
        connection.close()


def test_work_group_and_employee_require_real_party_account():
    connection = _database()
    constants = _constants()
    try:
        _run(connection, constants["WorkGroupInsert"],
             {"id": _id(), "code": "WG-A", "name": "شیفت صبح", "notes": None})
        connection.commit()
        with pytest.raises(sqlite3.IntegrityError):
            _run(connection, constants["EmployeeInsert"],
                 {"id": _id(), "partyAccount": _id(), "workGroup": _id(),
                  "job": "راننده", "notes": None})
        connection.rollback()

        party_id = _id()
        ledger_id = _id()
        account_id = _id()
        connection.execute("INSERT INTO parties(id,display_name) VALUES(?,?)",
                           (party_id, "راننده نمونه"))
        connection.execute(
            "INSERT INTO ledger_accounts(id,account_code,display_name,account_class,normal_side)"
            " VALUES(?,?,?,'CONTROL','DEBIT')", (ledger_id, "PTY000001", "راننده نمونه"))
        connection.execute(
            "INSERT INTO party_accounts(id,party_id,ledger_account_id) VALUES(?,?,?)",
            (account_id, party_id, ledger_id))
        connection.commit()

        _run(connection, constants["EmployeeInsert"],
             {"id": _id(), "partyAccount": account_id, "workGroup": None,
              "job": "راننده", "notes": None})
        connection.commit()
        assert connection.execute(
            "SELECT COUNT(*) FROM employees WHERE party_account_id=?", (account_id,)).fetchone()[0] == 1
    finally:
        connection.close()


def test_operational_parameter_upsert_is_idempotent():
    connection = _database()
    constants = _constants()
    values = {"id": _id(), "code": "FISCAL_YEAR_KIND", "name": "قاعده سال مالی",
              "intValue": None, "textValue": "JALALI", "kind": "TEXT", "unit": None,
              "effectiveFrom": "2026-01-01", "notes": "نیاز به تأیید مالک"}
    try:
        _run(connection, constants["ParameterUpsert"], values)
        values["textValue"] = "GREGORIAN"
        _run(connection, constants["ParameterUpsert"], values)
        connection.commit()
        rows = connection.execute(
            "SELECT value_text,value_kind FROM operational_parameters "
            "WHERE parameter_code='FISCAL_YEAR_KIND'").fetchall()
        assert rows == [("GREGORIAN", "TEXT")]
    finally:
        connection.close()


def test_bank_account_insert_creates_control_ledger_and_encrypted_payload():
    connection = _database()
    constants = _constants()
    try:
        connection.execute(
            "INSERT INTO bank_catalog(bank_code,display_name,is_system) VALUES('B1','بانک نمونه',1)")
        connection.commit()
        ledger_id = _id()
        account_id = _id()
        _run(connection, constants["BankLedgerInsert"],
             {"id": ledger_id, "code": "BNK000001", "name": "حساب جاری نمونه"})
        _run(connection, constants["BankAccountInsert"],
             {"id": account_id, "ledger": ledger_id, "title": "حساب جاری نمونه",
              "bank": "بانک نمونه", "branch": "مرکزی", "number": b"\x01\x02cipher",
              "iban": b"\x03\x04cipher", "owner": "مازندشت", "notes": None})
        connection.commit()
        row = connection.execute(
            "SELECT account_number_enc, iban_enc FROM bank_accounts WHERE id=?", (account_id,)).fetchone()
        assert row[0] == b"\x01\x02cipher" and row[1] == b"\x03\x04cipher"
        assert connection.execute(
            "SELECT COUNT(*) FROM ledger_accounts WHERE id=?", (ledger_id,)).fetchone()[0] == 1
    finally:
        connection.close()


def test_base_manage_permission_gate_matches_the_seeded_roles():
    connection = _database()
    manager_id, scale_id = _id(), "c" * 32
    try:
        _add_user(connection, manager_id, "manager1", "MANAGER")
        _add_user(connection, scale_id, "scale1", "SCALE")

        def allowed(user_id):
            positional, ordered = _bind(MANAGER_SQL, {"user": user_id})
            return connection.execute(positional, ordered).fetchone()[0]

        assert allowed(manager_id) == 1
        assert allowed(scale_id) == 0
        assert allowed(_id()) == 0
    finally:
        connection.close()


def test_audit_insert_shape_matches_the_schema():
    """The audit statement used by every write path must satisfy audit_log."""
    source = SERVICE.read_text(encoding="utf-8")
    match = re.search(r'INSERT INTO audit_log\(([^)]*)\)\s*VALUES\(([^)]*)\)', source)
    assert match, "audit_log statement not found"
    columns = [c.strip() for c in match.group(1).split(",")]
    placeholders = re.findall(r"@(\w+)", match.group(2))
    assert columns[:4] == ["id", "actor_user_id", "action_code", "entity_type"]
    required = {"id", "actor", "action", "entityType", "entityId", "after", "machine"}
    assert required <= set(placeholders), required - set(placeholders)


def test_base_data_window_is_reachable_from_the_main_shell():
    main_window = (APP / "MainWindow.xaml").read_text(encoding="utf-8")
    main_cs = (APP / "MainWindow.xaml.cs").read_text(encoding="utf-8")
    assert 'Click="OpenBaseData_Click"' in main_window
    assert "new BaseDataWindow(_databasePath, user.UserId)" in main_cs
    window_cs = (APP / "Views" / "BaseDataWindow.cs").read_text(encoding="utf-8")
    for tab in ("گروه‌های اصلی و زیرگروه", "انواع تراکنش", "درجه‌بندی و بسته‌بندی محصول",
                "گروه‌های کاری و کارکنان", "پارامترهای عملیاتی", "حساب‌های بانکی",
                "ماهیت حساب‌ها و گروه‌ها"):
        assert tab in window_cs, tab
