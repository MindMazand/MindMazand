# Mazandasht.Desktop — WPF source

Target: .NET Framework 4.8 / WPF  
Solution: `../Mazandasht.sln`

## Build
Open `Mazandasht.sln` in Visual Studio 2022 on Windows, restore NuGet packages, and build `Release|Any CPU`.

Pinned packages:
- System.Data.SQLite.Core 1.0.119
- ExcelDataReader 3.9.0
- Microsoft.NETFramework.ReferenceAssemblies.net48 1.0.3

## First run
The application creates its database under:
`%LOCALAPPDATA%\Mazandasht\Data\mazandasht.db`

If there is no user, a first-run window creates the first ADMIN account. No default username or password is shipped.

## Database migrations
- `Database/schema_v1.sql` — relational core schema
- `Database/system_seed.sql` — system roles and permissions only
- `Database/migration_002.sql` — source provenance and encrypted import payload
- `Database/migration_003.sql` — weighing defaults and stock posting guard
- `Database/migration_004.sql` — Excel column-mapping metadata

The application upgrades existing schema versions 1–3 to version 4. A clean reset rebuilds the same schema and system defaults without users or business records.

## Excel intake
The in-app Excel center supports 11 module profiles and copies the matching blank template to a user-selected path. It reads `.xlsx` / `.xls`, previews up to 250 rows, records SHA-256, supports editable source-to-target column mapping, and stores the selected profile and mapping with an encrypted preview batch. The source header must be the first non-empty row in the selected sheet.

Important: the current alpha substage stages and maps source rows; it does not yet validate every business rule or commit the 11 profiles to operational ledgers. The UI explicitly says that preview staging is not a posted operational import.

## Database maintenance
The ADMIN-only maintenance window shows table counts, requires a new backup path and exact confirmation phrase, creates and verifies a SQLite backup, rebuilds a clean schema, checks integrity and foreign keys, and then replaces the active database. After reset all users are gone; the next launch opens first-run administrator creation.

## Current limits
- This package has not yet been compiled or executed on Windows.
- No EXE/MSI installer is included.
- The full purchase, sales, sorting, inventory, basket, double-entry accounting, bank, cheque and settlement engine is still under Stage 05 development.
- The Excel staging queue is not yet the final import commit path.
