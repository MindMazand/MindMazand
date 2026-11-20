import sqlite3
from pathlib import Path
import xml.etree.ElementTree as ET
from openpyxl import load_workbook

ROOT = Path(__file__).resolve().parents[1]
APP = ROOT / "app" / "Mazandasht.Desktop"
TEMPLATES = APP / "Templates"


def clean_seed_database():
    c = sqlite3.connect(":memory:")
    c.execute("PRAGMA foreign_keys=ON")
    for name in ["schema_v1.sql", "system_seed.sql", "migration_002.sql", "migration_003.sql",
                 "migration_004.sql", "migration_005.sql", "migration_006.sql", "migration_007.sql"]:
        c.executescript((APP / "Database" / name).read_text(encoding="utf-8"))
    return c


def test_clean_database_seed_contains_system_defaults_only():
    c = clean_seed_database()
    try:
        assert c.execute("PRAGMA user_version").fetchone()[0] == 7
        assert c.execute("SELECT COUNT(*) FROM roles").fetchone()[0] >= 6
        assert c.execute("SELECT COUNT(*) FROM ledger_accounts").fetchone()[0] == 16
        assert c.execute("SELECT COUNT(*) FROM permissions").fetchone()[0] > 0
        assert c.execute("SELECT COUNT(*) FROM role_permissions").fetchone()[0] > 0
        for table in ["users", "parties", "party_accounts", "products", "documents", "weigh_tickets",
                      "stock_lots", "stock_movements", "journal_entries", "bank_accounts", "cheques",
                      "basket_movements", "import_batches", "import_rows", "source_file_catalog"]:
            assert c.execute(f"SELECT COUNT(*) FROM {table}").fetchone()[0] == 0, table
        assert c.execute("SELECT COUNT(*) FROM units WHERE unit_code IN ('KG','G')").fetchone()[0] == 2
        assert c.execute("SELECT COUNT(*) FROM operations WHERE operation_code IN ('SYS-WEIGH-IN','SYS-WEIGH-OUT')").fetchone()[0] == 2
        assert c.execute("SELECT COUNT(*) FROM bank_catalog").fetchone()[0] >= 10
        assert c.execute("SELECT COUNT(*) FROM bank_accounts").fetchone()[0] == 0
        assert c.execute("SELECT COUNT(*) FROM operations WHERE operation_code LIKE 'SYS-%'").fetchone()[0] >= 15
        assert c.execute("PRAGMA integrity_check").fetchone()[0] == "ok"
        assert c.execute("PRAGMA foreign_key_check").fetchall() == []
    finally:
        c.close()


def test_all_excel_templates_are_blank_and_have_data_dictionary():
    files = sorted(TEMPLATES.glob("*.xlsx"))
    assert len(files) == 11
    for path in files:
        wb = load_workbook(path, data_only=False, read_only=True)
        assert "راهنما" in wb.sheetnames
        assert "داده‌ها" in wb.sheetnames
        assert "فرهنگ داده" in wb.sheetnames
        ws = wb["داده‌ها"]
        headers = [ws.cell(1, col).value for col in range(1, ws.max_column + 1)]
        assert headers and all(isinstance(x, str) and x.strip() for x in headers)
        dictionary = wb["فرهنگ داده"]
        dictionary_fields = [dictionary.cell(row, 1).value for row in range(2, dictionary.max_row + 1)]
        dictionary_fields = [value for value in dictionary_fields if value]
        assert headers == dictionary_fields, f"header/dictionary mismatch: {path.name}"
        for row in ws.iter_rows(min_row=2, values_only=True):
            assert all(value is None for value in row), f"template contains data: {path.name}"
        wb.close()


def test_import_center_selects_module_profile_and_keeps_staging_non_operational():
    xaml = APP / "Views" / "ImportReviewWindow.xaml"
    code = APP / "Views" / "ImportReviewWindow.xaml.cs"
    catalog = APP / "Import" / "ImportProfileCatalog.cs"
    staging = APP / "Import" / "ImportStagingService.cs"
    for p in [xaml, APP / "MainWindow.xaml", APP / "Mazandasht.Desktop.csproj"]:
        ET.parse(p)
    xaml_text = xaml.read_text(encoding="utf-8")
    code_text = code.read_text(encoding="utf-8")
    catalog_text = catalog.read_text(encoding="utf-8")
    staging_text = staging.read_text(encoding="utf-8")
    assert "ProfileCombo" in xaml_text
    assert "Template_Click" in code_text
    assert "ImportProfileCatalog.All" in code_text
    assert "SavePreview(_databasePath, preview, profile.Code, mappingJson, headerRow)" in code_text
    assert "ImportProfileCatalog.Find(importProfile)" in staging_text
    assert "column_mapping_json" in staging_text
    assert "ReadAllRows(preview.FilePath, preview.SelectedSheet, headerRowNumber)" in staging_text
    assert "raw_sensitive_enc" in staging_text
    mapping_text = (APP / "Database" / "migration_004.sql").read_text(encoding="utf-8")
    assert "column_mapping_json" in mapping_text and "header_row_number" in mapping_text
    assert "PURCHASE_INVOICES" in catalog_text and "BANK_STATEMENT" in catalog_text
    assert "این مرحله هنوز داده را به دفاتر عملیاتی منتقل نمی‌کند" in code_text


def test_database_reset_is_admin_gated_backed_up_and_rebuilds_clean_schema():
    service = (APP / "Database" / "DatabaseResetService.cs").read_text(encoding="utf-8")
    ui = (APP / "Views" / "DatabaseMaintenanceWindow.cs").read_text(encoding="utf-8")
    main = (APP / "MainWindow.xaml.cs").read_text(encoding="utf-8")
    assert "ConfirmationPhrase" in service
    assert "RequireActiveAdmin" in service
    assert "BackupDatabase" in service
    assert "integrity_check" in service
    assert "foreign_key_check" in service
    assert "systemTables" in service
    assert "schema_v1.sql" in service and "system_seed.sql" in service and "migration_004.sql" in service
    assert "DELETE FROM" not in service.upper()
    assert "ResetCompleted" in ui
    assert "پاکسازی کامل فقط توسط مدیر سیستم" in service
    assert "Application.Current.Shutdown()" in main


def test_schema_v5_has_sample_bank_names_but_no_real_bank_accounts():
    c = clean_seed_database()
    try:
        banks = [row[0] for row in c.execute("SELECT display_name FROM bank_catalog ORDER BY display_name")]
        assert "بانک صادرات" in banks
        assert "بانک ملی ایران" in banks
        assert c.execute("SELECT COUNT(*) FROM bank_accounts").fetchone()[0] == 0
        columns = [row[1] for row in c.execute("PRAGMA table_info(bank_accounts)")]
        assert "bank_catalog_id" in columns
    finally:
        c.close()


def test_reset_and_bootstrap_both_apply_latest_schema():
    bootstrap = (APP / "Database" / "DatabaseBootstrapper.cs").read_text(encoding="utf-8")
    reset = (APP / "Database" / "DatabaseResetService.cs").read_text(encoding="utf-8")
    assert 'version < 5' in bootstrap and 'Database/migration_005.sql' in bootstrap
    assert 'version < 6' in bootstrap and 'Database/migration_006.sql' in bootstrap
    assert 'version < 7' in bootstrap and 'Database/migration_007.sql' in bootstrap
    assert 'version != 7' in bootstrap
    assert 'Database/migration_005.sql' in reset and 'Database/migration_006.sql' in reset
    assert 'Database/migration_007.sql' in reset
    assert 'version != 7' in reset
    assert '"bank_catalog"' in reset
    assert '"ledger_accounts"' in reset


def test_maintenance_button_is_hidden_for_non_admin_and_still_server_guarded():
    xaml = (APP / "MainWindow.xaml").read_text(encoding="utf-8")
    main = (APP / "MainWindow.xaml.cs").read_text(encoding="utf-8")
    reset = (APP / "Database" / "DatabaseResetService.cs").read_text(encoding="utf-8")
    assert 'x:Name="DatabaseMaintenanceButton"' in xaml
    assert 'DatabaseMaintenanceButton.Visibility = isAdmin ? Visibility.Visible : Visibility.Collapsed' in main
    assert 'RequireActiveAdmin' in reset


def test_first_run_import_prompt_is_optional_and_persisted_once():
    app_start = (APP / "App.xaml.cs").read_text(encoding="utf-8")
    setup = (APP / "Services" / "InitialSetupService.cs").read_text(encoding="utf-8")
    assert "TryMarkInitialImportPromptShown" in app_start
    assert "MessageBoxButton.YesNo" in app_start
    assert "INITIAL_IMPORT_PROMPT_SHOWN" in setup
    assert "app_settings" in setup


def test_csv_import_is_wired_and_bounded():
    reader = (APP / "Import" / "ImportWorkbookReader.cs").read_text(encoding="utf-8")
    ui = (APP / "Views" / "ImportReviewWindow.xaml.cs").read_text(encoding="utf-8")
    assert '".csv"' in reader
    assert "DetectDelimiter" in reader
    assert "MaximumRowsPerSheet" in reader and "MaximumColumns" in reader
    assert "Windows-1256" not in reader  # implementation uses Encoding.GetEncoding(1256)
    assert "Encoding.GetEncoding(1256)" in reader
    assert "*.csv" in ui


def test_bank_and_operation_catalogues_are_editable_without_creating_transactions():
    master = (APP / "Views" / "OperationalWindows.cs").read_text(encoding="utf-8")
    catalog = (APP / "Services" / "SystemCatalogService.cs").read_text(encoding="utf-8")
    assert 'Header="فهرست بانک‌ها"' in master
    assert 'Header="انواع عملیات"' in master
    assert "AddBank" in catalog and "AddOperation" in catalog
    assert "base.manage" in catalog


def test_v4_to_v5_upgrade_is_additive_and_preserves_existing_settings():
    c = sqlite3.connect(":memory:")
    c.execute("PRAGMA foreign_keys=ON")
    for name in ["schema_v1.sql", "system_seed.sql", "migration_002.sql", "migration_003.sql", "migration_004.sql"]:
        c.executescript((APP / "Database" / name).read_text(encoding="utf-8"))
    c.execute("INSERT INTO app_settings(setting_key,value_json,category) VALUES('KEEP_ME','\"persist\"','TEST')")
    c.commit()
    c.executescript((APP / "Database" / "migration_005.sql").read_text(encoding="utf-8"))
    assert c.execute("PRAGMA user_version").fetchone()[0] == 5
    assert c.execute("SELECT value_json FROM app_settings WHERE setting_key='KEEP_ME'").fetchone()[0] == '"persist"'
    assert c.execute("SELECT COUNT(*) FROM bank_catalog").fetchone()[0] == 12
    assert c.execute("SELECT COUNT(*) FROM bank_accounts").fetchone()[0] == 0
    assert c.execute("PRAGMA integrity_check").fetchone()[0] == "ok"
    assert c.execute("PRAGMA foreign_key_check").fetchall() == []
    c.close()
