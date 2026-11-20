# Security Notes — Mazandasht

## Implemented in source
- No default administrator credentials; first-run setup creates the first ADMIN only when `users` is empty.
- Passwords use PBKDF2-HMAC-SHA256 with independent salts.
- SQL user inputs are parameterized in the inspected services.
- Database uses foreign keys, WAL, FULL synchronous and busy timeout.
- Sensitive import-row payloads are protected with DPAPI `CurrentUser`; `raw_row_json` stores non-sensitive metadata only.
- Full database reset is ADMIN-only and rechecks the active administrator in the database immediately before the operation.
- Reset requires an external backup path, refuses to overwrite an existing backup, checks the backup and rebuilt database, and verifies foreign keys before swapping files.

## Operational cautions
- DPAPI `CurrentUser` binds encrypted import payloads to the Windows user profile. Moving a staged database to another Windows account requires a controlled re-encryption process.
- The reset backup contains all pre-reset database data and must be protected as confidential. Store it on an access-controlled drive.
- Reset removes all users; after restart, the first-run administrator must be created again.
- Do not store plaintext PII in `import_rows.raw_row_json`.
- The Excel staging queue is not the same as an approved operational import. Module-specific validation and transactional commit must be completed before production use.
- Secure backup encryption, automatic restore UI, failed-login throttling, tamper-evident audit signing, code signing and installer integrity verification are not implemented in this package.

## Data handling
- No real business data or source workbook rows are seeded in the operational database or in the blank Excel templates.
- Source audit workbooks and source-index databases are not part of the Stage 05 runtime release package.
