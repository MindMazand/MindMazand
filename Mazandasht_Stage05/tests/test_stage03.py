import sqlite3
import tempfile
import xml.etree.ElementTree as ET
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]
APP = ROOT / 'app' / 'Mazandasht.Desktop'

def test_import_staging_schema_contract():
    with tempfile.TemporaryDirectory(prefix='mzd-stage03-') as td:
        db = Path(td) / 'stage.sqlite'
        con = sqlite3.connect(db)
        con.execute('PRAGMA foreign_keys=ON')
        con.executescript((APP/'Database'/'schema_v1.sql').read_text(encoding='utf-8'))
        con.executescript((APP/'Database'/'system_seed.sql').read_text(encoding='utf-8'))
        con.executescript((APP/'Database'/'migration_002.sql').read_text(encoding='utf-8'))
        con.execute("INSERT INTO users(id,username,display_name,password_hash,password_salt,password_iterations,role_id) VALUES(?,?,?,?,?,?,?)",
                    ('u-stage','stage-admin','Stage Admin',b'hash',b'salt',600000,'role-admin'))
        digest = 'a' * 64
        con.execute("INSERT INTO source_file_catalog(source_sha256,source_file_name,source_size_bytes,source_kind,sheet_count,imported_by) VALUES(?,?,?,?,?,?)",
                    (digest,'source.xlsx',1234,'OTHER',2,'u-stage'))
        con.execute("INSERT INTO import_batches(id,source_file_name,source_file_sha256,source_sheet_name,import_profile,status,total_rows,accepted_rows,rejected_rows,created_by) VALUES(?,?,?,?,?,?,?,?,?,?)",
                    ('batch1','source.xlsx',digest,'Sheet1','GENERIC_REVIEW_ONLY','PREVIEW',1,0,0,'u-stage'))
        safe = '{"sourceSheet":"Sheet1","sourceRowNumber":7,"cellCount":3,"sensitivePayloadEncrypted":true}'
        con.execute("INSERT INTO import_rows(id,import_batch_id,source_row_number,row_status,raw_row_json,source_row_sha256,raw_sensitive_enc) VALUES(?,?,?,?,?,?,?)",
                    ('row1','batch1',7,'PENDING',safe,'b'*64,b'encrypted-payload'))
        row = con.execute('SELECT row_status,raw_row_json,raw_sensitive_enc FROM import_rows WHERE id=?',('row1',)).fetchone()
        assert row[0] == 'PENDING'
        assert 'encrypted-payload' not in row[1]
        assert row[2] == b'encrypted-payload'
        assert con.execute("SELECT status FROM import_batches WHERE id='batch1'").fetchone()[0] == 'PREVIEW'
        assert con.execute('PRAGMA integrity_check').fetchone()[0] == 'ok'
        assert con.execute('PRAGMA foreign_key_check').fetchall() == []
        con.close()

def test_import_ui_and_crypto_contract():
    for p in [APP/'Views'/'ImportReviewWindow.xaml', APP/'App.xaml', APP/'Mazandasht.Desktop.csproj', APP/'MainWindow.xaml']:
        ET.parse(p)
    reader = (APP/'Import'/'ImportWorkbookReader.cs').read_text(encoding='utf-8')
    staging = (APP/'Import'/'ImportStagingService.cs').read_text(encoding='utf-8')
    xaml = (APP/'Views'/'ImportReviewWindow.xaml').read_text(encoding='utf-8')
    code = (APP/'Views'/'ImportReviewWindow.xaml.cs').read_text(encoding='utf-8')
    assert 'MaximumRowsPerSheet = 10000' in reader
    assert 'ComputeSha256' in reader
    assert 'ProtectedData.Protect' in staging
    assert 'DataProtectionScope.CurrentUser' in staging
    assert 'RoleCode' in staging and 'MANAGER' in staging and 'ADMIN' in staging
    assert 'ComputeSha256(preview.FilePath)' in staging
    assert 'raw_sensitive_enc' in staging
    assert 'raw_row_json' in staging
    assert 'ذخیره در صف بررسی' in xaml
    assert 'ImportStagingService.SavePreview' in code
    assert 'هیچ رکورد عملیاتی یا سند مالی ایجاد نشده است' in code
    handlers = set(re.findall(r'(?:Click|SelectionChanged)="([A-Za-z_][A-Za-z0-9_]*)"', xaml))
    methods = set(re.findall(r'void\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(', code))
    assert handlers.issubset(methods), handlers-methods

if __name__ == '__main__':
    test_import_staging_schema_contract()
    test_import_ui_and_crypto_contract()
    print('PASS: import staging schema contract, encrypted payload contract, XAML and handlers.')
