-- Migration 004: module-specific Excel import mapping metadata.
BEGIN IMMEDIATE;

ALTER TABLE import_batches ADD COLUMN column_mapping_json TEXT;
ALTER TABLE import_batches ADD COLUMN header_row_number INTEGER NOT NULL DEFAULT 1 CHECK(header_row_number > 0);
ALTER TABLE import_batches ADD COLUMN validation_summary_json TEXT;

INSERT OR IGNORE INTO schema_migrations(version, name)
VALUES (4, 'Module-specific Excel import mapping metadata');
UPDATE app_metadata SET meta_value='4', updated_at_utc=strftime('%Y-%m-%dT%H:%M:%fZ','now')
WHERE meta_key='schema_version';
PRAGMA user_version=4;
COMMIT;
