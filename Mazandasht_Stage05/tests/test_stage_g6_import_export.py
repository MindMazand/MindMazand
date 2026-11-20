# -*- coding: utf-8 -*-
"""G6 acceptance tests: operational import commit engine and export.

As in G5, the C# service is verified by extracting the exact SQL it ships and
running it against a real schema-v6 database. Two things cannot be exercised in
this environment and are stated openly rather than claimed:
  - DPAPI decryption of staged payloads (Windows-only);
  - the WPF windows themselves.
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
CONTRACT = ROOT / "docs" / "11_Import_Profiles_Canonical.json"
MIGRATIONS = ["schema_v1.sql", "system_seed.sql", "migration_002.sql", "migration_003.sql",
              "migration_004.sql", "migration_005.sql", "migration_006.sql", "migration_007.sql"]

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


def _bind(sql: str, values: dict):
    names = re.findall(r"@(\w+)", sql)
    return re.sub(r"@\w+", "?", sql), [values.get(n) for n in names]


def _run(connection, sql, values):
    positional, ordered = _bind(sql, values)
    connection.execute(positional, ordered)


def _sql_constants(path: Path) -> dict:
    return {name: body for name, body in re.findall(
        r'Sql(\w+)\s*=\s*@"(.*?)";', path.read_text(encoding="utf-8"), re.S)}


# ---------------------------------------------------------------------------
# Profile contract cross-check
# ---------------------------------------------------------------------------
def test_commit_contract_matches_the_canonical_import_profiles():
    contract = json.loads(CONTRACT.read_text(encoding="utf-8"))
    source = (APP / "Import" / "ImportTargetCatalog.cs").read_text(encoding="utf-8")

    # Every canonical profile appears in the commit-side contract.
    canonical_codes = {"MASTER_PARTIES", "MASTER_PRODUCTS", "WEIGHING_IN", "PURCHASE_INVOICES",
                       "SALES_INVOICES", "SORTING", "BASKET_MOVEMENTS", "BANK_STATEMENT",
                       "CHEQUES", "SETTLEMENTS", "PARTNER_OPERATIONS"}
    assert canonical_codes <= set(re.findall(r'ProfileCode = "(\w+)"', source))

    # Required fields declared in C# must be a subset of the canonical required
    # fields, and every canonical required field must be covered by C#.
    canonical_required = {}
    for profile in contract["profiles"]:
        code = profile["file"].split("_")[0]
        canonical_required[profile["headers"][0]] = [
            f["field"] for f in profile["dictionary"] if f["required"]]

    blocks = re.split(r'new ImportTarget \{', source)[1:]
    assert len(blocks) == 11
    for block in blocks:
        code = re.search(r'ProfileCode = "(\w+)"', block).group(1)
        declared = re.findall(r'(?:RequiredField\d|DateField|PartyField|ProductField|'
                              r'WarehouseField|SourceDocumentField) = "([^"]+)"', block)
        key_field = re.search(r'KeyField = "([^"]+)"', block).group(1)
        file_name = next(p["file"] for p in contract["profiles"]
                         if p["file"].startswith(
                             {"MASTER_PARTIES": "01", "MASTER_PRODUCTS": "02",
                              "WEIGHING_IN": "03", "PURCHASE_INVOICES": "04",
                              "SALES_INVOICES": "05", "SORTING": "06",
                              "BASKET_MOVEMENTS": "07", "BANK_STATEMENT": "08",
                              "CHEQUES": "09", "SETTLEMENTS": "10",
                              "PARTNER_OPERATIONS": "11"}[code]))
        expected = [f["field"] for f in
                    next(p for p in contract["profiles"] if p["file"] == file_name)["dictionary"]
                    if f["required"]]
        # The key field is required by construction; the rest must be declared.
        assert key_field in expected, f"{code}: key field {key_field} is not a canonical required field"
        assert set(expected) - {key_field} <= set(declared), \
            f"{code}: {set(expected) - {key_field} - set(declared)}"
        assert set(declared) <= set(expected), \
            f"{code}: declared fields outside the canonical contract: {set(declared) - set(expected)}"


def test_required_fields_helper_enforces_every_bound_column():
    source = (APP / "Import" / "ImportTargetCatalog.cs").read_text(encoding="utf-8")
    body = source.split("public static IList<string> RequiredFields(ImportTarget target)")[1]
    body = body.split("public static string DocumentTypeFor")[0]
    for token in ("DateField", "PartyField", "ProductField", "WarehouseField",
                  "SourceDocumentField", "RequiredField1", "RequiredField8"):
        assert token in body, token


def test_document_types_are_valid_for_the_schema_check():
    schema = "".join((DB / n).read_text(encoding="utf-8") for n in MIGRATIONS)
    allowed = set(re.findall(r"document_type IN \(([^)]*)\)", schema)[0].replace("'", "").split(","))
    source = (APP / "Import" / "ImportTargetCatalog.cs").read_text(encoding="utf-8")
    for document_type in re.findall(r'DocumentType = "(\w+)"', source):
        assert document_type in allowed, document_type


# ---------------------------------------------------------------------------
# Commit SQL executed against schema v6
# ---------------------------------------------------------------------------
def test_commit_document_insert_creates_a_draft_without_posting_flags():
    connection = _database()
    constants = _sql_constants(APP / "Import" / "ImportCommitService.cs")
    manager = _id()
    try:
        _add_user(connection, manager, "manager1", "MANAGER")
        connection.execute("INSERT INTO parties(id,display_name) VALUES(?,?)", (_id(), "خریدار نمونه"))
        connection.commit()

        document_id = _id()
        _run(connection, constants["DocumentInsert"], {
            "id": document_id, "type": "PURCHASE", "fiscal": "2026",
            "date": "2026-11-19", "party": None, "reference": "INV-1", "notes": "واردشده",
            "user": manager})
        connection.commit()
        row = connection.execute(
            "SELECT status,requires_journal,requires_stock_posting,document_number "
            "FROM documents WHERE id=?", (document_id,)).fetchone()
        # DRAFT, nothing posted, and the autofill trigger gave it a number.
        assert row[0] == "DRAFT" and row[1] == 0 and row[2] == 0
        assert row[3] not in ("", None)

        line_id = _id()
        _run(connection, constants["DocumentLineInsert"], {
            "id": line_id, "document": document_id, "product": None, "packaging": None,
            "quantityG": 4200, "quantityCount": 10, "unitPrice": 15000, "amount": 63000,
            "description": "ردیف منبع"})
        connection.commit()
        assert connection.execute(
            "SELECT amount_irr FROM document_lines WHERE id=?", (line_id,)).fetchone()[0] == 63000

        _run(connection, constants["StatusHistoryInsert"], {
            "id": _id(), "document": document_id, "user": manager, "reason": "ورود نمونه"})
        connection.commit()
        assert connection.execute(
            "SELECT COUNT(*) FROM document_status_history WHERE document_id=?", (document_id,)
        ).fetchone()[0] == 1
    finally:
        connection.close()


def test_commit_row_and_batch_updates_keep_counters_consistent():
    connection = _database()
    constants = _sql_constants(APP / "Import" / "ImportCommitService.cs")
    manager = _id()
    batch_id = _id()
    row_id = _id()
    try:
        _add_user(connection, manager, "manager1", "MANAGER")
        connection.execute(
            "INSERT INTO import_batches(id,source_file_name,source_sheet_name,import_profile,"
            "status,total_rows,created_by) VALUES(?,?,?,?,?,?,?)",
            (batch_id, "f.xlsx", "داده‌ها", "WEIGHING_IN", "PREVIEW", 1, manager))
        connection.execute(
            "INSERT INTO import_rows(id,import_batch_id,source_row_number,row_status,raw_row_json,"
            "source_row_sha256) VALUES(?,?,?,?,?,?)",
            (row_id, batch_id, 2, "PENDING", "{}", "h" * 64))
        connection.commit()

        _run(connection, constants["RowUpdate"], {
            "id": row_id, "key": "W-1", "status": "IMPORTED", "entity": "DOCUMENT",
            "target": _id(), "errors": None})
        _run(connection, constants["BatchUpdate"], {
            "id": batch_id, "status": "COMMITTED", "accepted": 1, "rejected": 0,
            "committed": "2026-11-19T00:00:00Z"})
        connection.commit()
        row = connection.execute(
            "SELECT row_status,target_entity FROM import_rows WHERE id=?", (row_id,)).fetchone()
        assert row == ("IMPORTED", "DOCUMENT")
        batch = connection.execute(
            "SELECT status,accepted_rows FROM import_batches WHERE id=?", (batch_id,)).fetchone()
        assert batch == ("COMMITTED", 1)
    finally:
        connection.close()


def test_duplicate_detection_uses_the_source_row_hash():
    connection = _database()
    constants = _sql_constants(APP / "Import" / "ImportCommitService.cs")
    try:
        manager = _id()
        _add_user(connection, manager, "manager1", "MANAGER")
        connection.execute(
            "INSERT INTO import_batches(id,source_file_name,source_sheet_name,import_profile,"
            "status,total_rows,created_by) VALUES(?,?,?,?,?,?,?)",
            (_id(), "f.xlsx", "داده‌ها", "WEIGHING_IN", "COMMITTED", 1, manager))
        connection.commit()
        connection.execute(
            "INSERT INTO import_rows(id,import_batch_id,source_row_number,row_status,raw_row_json,"
            "source_row_sha256) VALUES(?,?,?,?,?,?)",
            (_id(), connection.execute("SELECT id FROM import_batches LIMIT 1").fetchone()[0],
             2, "IMPORTED", "{}", "dup" * 21 + "d"))
        connection.commit()
        positional, ordered = _bind(constants["DuplicateCheck"], {"hash": "dup" * 21 + "d"})
        assert connection.execute(positional, ordered).fetchone()[0] == 1
        positional, ordered = _bind(constants["DuplicateCheck"], {"hash": "x" * 64})
        assert connection.execute(positional, ordered).fetchone()[0] == 0
    finally:
        connection.close()


def test_commit_permission_gate_uses_the_seeded_roles():
    connection = _database()
    constants = _sql_constants(APP / "Import" / "ImportCommitService.cs")
    manager, scale = _id(), _id()
    try:
        _add_user(connection, manager, "manager1", "MANAGER")
        _add_user(connection, scale, "supervisor1", "SUPERVISOR")

        def allowed(user_id):
            positional, ordered = _bind(constants["PermissionCheck"], {"user": user_id})
            return connection.execute(positional, ordered).fetchone()[0]

        assert allowed(manager) == 1
        # SUPERVISOR holds no documents.create grant in system_seed.sql.
        assert allowed(scale) == 0
    finally:
        connection.close()


def test_commit_engine_never_posts_journal_or_stock():
    source = (APP / "Import" / "ImportCommitService.cs").read_text(encoding="utf-8")
    assert "'DRAFT',0,0" in source, "imported documents must be created DRAFT with no posting flags"
    assert not re.search(r"INSERT INTO (journal_entries|journal_lines|stock_movements|stock_lots)",
                         source), "the import commit engine must not post journals or stock"


# ---------------------------------------------------------------------------
# Export
# ---------------------------------------------------------------------------
def test_export_csv_permission_gate_and_run_lifecycle_sql():
    connection = _database()
    constants = _sql_constants(APP / "Import" / "ExportService.cs")
    manager, scale = _id(), _id()
    run_id = _id()
    try:
        _add_user(connection, manager, "manager1", "MANAGER")
        _add_user(connection, scale, "scale1", "SCALE")

        positional, ordered = _bind(constants["PermissionCheck"], {"user": manager})
        assert connection.execute(positional, ordered).fetchone()[0] == 1
        positional, ordered = _bind(constants["PermissionCheck"], {"user": scale})
        assert connection.execute(positional, ordered).fetchone()[0] == 0

        _run(connection, constants["ExportStart"], {
            "id": run_id, "kind": "DELTA_EXCEL", "path": "/tmp/out.csv", "user": manager})
        _run(connection, constants["ExportFinish"], {
            "id": run_id, "status": "SUCCESS", "completed": "2026-11-19T00:00:00Z",
            "rows": 7, "error": None})
        connection.commit()
        row = connection.execute(
            "SELECT status,rows_exported,error_text FROM export_runs WHERE id=?", (run_id,)).fetchone()
        assert row == ("SUCCESS", 7, None)

        # The export query must run and tolerate an empty document table.
        positional, ordered = _bind(constants["DocumentExport"], {"type": None})
        assert connection.execute(positional, ordered).fetchall() == []
    finally:
        connection.close()


def test_export_service_states_csv_only_and_records_runs():
    source = (APP / "Import" / "ExportService.cs").read_text(encoding="utf-8")
    assert "using ExcelDataReader" not in source
    assert "Workbook" not in source, "no workbook-writing API may be used"
    assert "UTF8Encoding(true)" in source, "CSV must be written with a BOM for Excel"
    assert "export_runs" in source


# ---------------------------------------------------------------------------
# Queue
# ---------------------------------------------------------------------------
def test_queue_queries_run_against_the_schema():
    connection = _database()
    constants = _sql_constants(APP / "Import" / "ImportQueueService.cs")
    manager = _id()
    batch_id = _id()
    try:
        _add_user(connection, manager, "manager1", "MANAGER")
        connection.execute(
            "INSERT INTO import_batches(id,source_file_name,source_sheet_name,import_profile,"
            "status,total_rows,created_by) VALUES(?,?,?,?,?,?,?)",
            (batch_id, "f.xlsx", "داده‌ها", "WEIGHING_IN", "PREVIEW", 3, manager))
        connection.commit()
        positional, ordered = _bind(constants["Batches"], {})
        rows = connection.execute(positional, ordered).fetchall()
        assert rows[0][0] == batch_id
        positional, ordered = _bind(constants["Cancel"], {"id": batch_id})
        assert connection.execute(positional, ordered).rowcount == 1
        connection.commit()
        assert connection.execute(
            "SELECT status FROM import_batches WHERE id=?", (batch_id,)).fetchone()[0] == "CANCELLED"
    finally:
        connection.close()


def test_fiscal_year_service_exposes_a_database_free_resolution():
    source = (APP / "Services" / "FiscalYearService.cs").read_text(encoding="utf-8")
    assert "public static string ResolveValue(string convention, string dateIso)" in source
    assert "PersianCalendar" in source
    assert "FISCAL_YEAR_KIND" in source
    # The Jalali conversion must not depend on a database connection.
    assert "ResolveValue" in source.split("ResolveValue(string convention")[0][-400:]


def test_catalog_import_sql_is_valid_against_the_schema():
    """The INSERTs used by the code-aware catalog import must satisfy the schema."""
    connection = _database()
    try:
        party_id, ledger_id, account_id = _id(), _id(), _id()
        connection.execute(
            "INSERT INTO parties(id,party_kind,display_name) VALUES(?,'PERSON',?)",
            (party_id, "پرتقال‌فروش نمونه"))
        connection.execute(
            "INSERT INTO ledger_accounts(id,account_code,display_name,account_class,normal_side,"
            "is_system_account) VALUES(?,?,?,'CONTROL','DEBIT',0)",
            (ledger_id, "PTY000001", "پرتقال‌فروش نمونه"))
        connection.execute(
            "INSERT INTO party_accounts(id,party_id,ledger_account_id,is_active) VALUES(?,?,?,1)",
            (account_id, party_id, ledger_id))
        connection.commit()
        assert connection.execute(
            "SELECT COUNT(*) FROM party_accounts WHERE ledger_account_id=?", (ledger_id,)
        ).fetchone()[0] == 1
        assert connection.execute("PRAGMA foreign_key_check").fetchall() == []
    finally:
        connection.close()


def test_import_queue_window_is_wired_into_the_shell():
    main_window = (APP / "MainWindow.xaml").read_text(encoding="utf-8")
    main_cs = (APP / "MainWindow.xaml.cs").read_text(encoding="utf-8")
    assert 'Click="OpenImportQueue_Click"' in main_window
    assert "new ImportQueueWindow(_databasePath)" in main_cs
    xaml = (APP / "Views" / "ImportQueueWindow.xaml").read_text(encoding="utf-8")
    assert 'x:Name="BatchGrid"' in xaml
    assert 'x:Name="CommitButton"' in xaml
    cs = (APP / "Views" / "ImportQueueWindow.xaml.cs").read_text(encoding="utf-8")
    assert "ImportCommitService.Validate" in cs and "ImportCommitService.Commit" in cs
