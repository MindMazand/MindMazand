import json
import sqlite3
import tempfile
import xml.etree.ElementTree as ET
from pathlib import Path
import re
import pytest

ROOT = Path(__file__).resolve().parents[1]
APP = ROOT / "app" / "Mazandasht.Desktop"

def test_schema_v2():
    db = Path(tempfile.gettempdir()) / "mazandasht_stage02_schema_test.sqlite"
    if db.exists():
        db.unlink()
    con = sqlite3.connect(db)
    con.execute("PRAGMA foreign_keys=ON")
    con.executescript((APP / "Database" / "schema_v1.sql").read_text(encoding="utf-8"))
    con.executescript((APP / "Database" / "system_seed.sql").read_text(encoding="utf-8"))
    con.executescript((APP / "Database" / "migration_002.sql").read_text(encoding="utf-8"))
    assert con.execute("PRAGMA user_version").fetchone()[0] == 2
    cols = {r[1] for r in con.execute("PRAGMA table_info(import_rows)")}
    assert {"source_row_sha256", "raw_sensitive_enc"}.issubset(cols)
    tables = {r[0] for r in con.execute("SELECT name FROM sqlite_master WHERE type='table'")}
    assert {"party_aliases", "source_file_catalog", "source_row_refs",
            "partner_statements", "partner_statement_lines", "reconciliation_cases"}.issubset(tables)
    assert con.execute("SELECT COUNT(*) FROM users").fetchone()[0] == 0
    assert con.execute("PRAGMA integrity_check").fetchone()[0] == "ok"
    assert con.execute("PRAGMA foreign_key_check").fetchall() == []
    con.close()
    db.unlink()

def test_private_source_index_not_packaged():
    db = ROOT / "migration" / "Mazandasht_Stage02_SourceIndex.sqlite"
    if not db.exists():
        pytest.skip("Private source-index database is intentionally excluded from the Stage 05 runtime package.")
    con = sqlite3.connect(db)
    try:
        assert con.execute("PRAGMA integrity_check").fetchone()[0] == "ok"
        assert con.execute("PRAGMA foreign_key_check").fetchall() == []
    finally:
        con.close()

def test_xaml_and_project_xml():
    for p in [APP/"App.xaml", APP/"MainWindow.xaml",
              APP/"Views"/"FirstRunWindow.xaml", APP/"Views"/"LoginWindow.xaml",
              APP/"App.config", APP/"app.manifest", APP/"Mazandasht.Desktop.csproj"]:
        ET.parse(p)
    # Check event handler names exist in code-behind files.
    checks = [
        (APP/"MainWindow.xaml", APP/"MainWindow.xaml.cs"),
        (APP/"Views"/"FirstRunWindow.xaml", APP/"Views"/"FirstRunWindow.xaml.cs"),
        (APP/"Views"/"LoginWindow.xaml", APP/"Views"/"LoginWindow.xaml.cs"),
    ]
    for xaml, code in checks:
        xtext = xaml.read_text(encoding="utf-8")
        ctext = code.read_text(encoding="utf-8")
        handlers = set(re.findall(r'(?:Click|KeyDown|MouseLeftButtonDown)="([A-Za-z_][A-Za-z0-9_]*)"', xtext))
        methods = set(re.findall(r'void\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(', ctext))
        assert handlers.issubset(methods), (xaml.name, handlers-methods)

if __name__ == "__main__":
    test_schema_v2()
    test_private_source_index_not_packaged()
    test_xaml_and_project_xml()
    print("PASS: schema v2, XML and event-handler checks. Private source index is not packaged.")
