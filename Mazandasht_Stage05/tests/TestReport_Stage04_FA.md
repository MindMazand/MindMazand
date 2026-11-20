# گزارش آزمون مرحله ۰.۴

آخرین اجرا: 2026-11-15

- `pytest -q`: 8 passed.
- SQLite migration 001 → 002 → 003: passed.
- `PRAGMA integrity_check`: ok.
- `PRAGMA foreign_key_check`: empty.
- Approval gating: passed at SQL trigger level.
- Stock posting before approval: rejected.
- Inbound stock posting after approval: passed.
- Outbound FIFO decrement: passed.
- Negative stock posting: rejected.
- XAML XML parse: passed.
- MainWindow XAML event handler coverage: 11/11.

توجه: آزمون‌ها جایگزین کامپایل C# و تست UI روی Windows نیستند.
