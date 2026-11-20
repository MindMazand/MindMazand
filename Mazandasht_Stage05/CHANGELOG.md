# Change Log

## 0.5.1-alpha2 — 2026-11-16

- Added schema migration 005 for a sample bank-institution catalogue and expanded default operation taxonomy.
- Seeded 12 bank institution names only; no real bank accounts, IBANs, owners, balances or transactions are created.
- Added visible settings tabs for bank names and operation types, with manager-authorized extension.
- Hid the destructive database maintenance navigation item from non-ADMIN users while retaining service-side ADMIN enforcement.
- Added a persisted, optional one-time initial Excel/CSV import prompt after the first administrator setup/login.
- Added CSV reading with comma/semicolon/tab delimiter detection, quoted-field parsing, UTF-8/UTF-16 and Windows-1256 fallback, and existing size/row/column limits.
- Updated reset rebuild to schema version 5 and retained system bank/operation catalogues.
- Added dashboard concept image as non-runtime visual reference.
- Verified SQLite migrations, integrity and foreign keys; 17 automated tests passed and 1 skipped.
- Windows WPF build remains unverified because no .NET SDK, MSBuild or C# compiler is available in this environment.
- Operational commit of imported rows is still not implemented for all 11 profiles.


# Changelog

## 0.5.0-alpha1 — Stage 05 Excel intake and database maintenance foundation
- Added 11 blank, module-specific Excel templates with guide, data and data-dictionary sheets.
- Added import profile selection and in-app template export in the Excel import center.
- Linked selected import profile to encrypted preview staging; the UI explicitly states that staging is not an operational commit.
- Added administrator-only full database reset with pre-reset row-count preview, mandatory SQLite backup, integrity/FK verification, clean schema rebuild and reset receipt.
- Added tests for clean bootstrap state, blank templates, import profile contracts and reset safety guards.
- No real source workbook rows were imported or seeded.

## 0.4.0 — Stage 04 six operational axes
- Added migration 003: document sequences, default weigh operations/units, unique active product name per group, non-negative stock posting trigger.
- Added master-data services for parties, product groups, products, packaging and warehouses.
- Added DPAPI-protected personal data and HMAC-based national-ID duplicate lookup.
- Added quick-add dialog and master-data tabs.
- Added weighing entry with integer gram storage, tare snapshot, loss calculation and FIFO stock allocation.
- Added manager-only approval window and transactional posting of pending stock movements.
- Added dashboard KPIs and seven-day approved weighing chart.
