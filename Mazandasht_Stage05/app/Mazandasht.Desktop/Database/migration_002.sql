
-- Mazandasht schema migration 002
-- Adds source provenance, bank aliases, partner statements and reconciliation queue.
-- Sensitive source payload MUST be stored in raw_sensitive_enc, never plaintext raw_row_json.

BEGIN IMMEDIATE;

ALTER TABLE import_rows ADD COLUMN source_row_sha256 TEXT;
ALTER TABLE import_rows ADD COLUMN raw_sensitive_enc BLOB;

CREATE TABLE IF NOT EXISTS source_file_catalog (
    source_sha256        TEXT PRIMARY KEY CHECK (length(source_sha256) = 64),
    source_file_name     TEXT NOT NULL,
    source_size_bytes    INTEGER NOT NULL CHECK (source_size_bytes >= 0),
    source_kind          TEXT NOT NULL CHECK (source_kind IN ('MASTER','OPERATIONAL','BANK_STATEMENT','PARTNER_STATEMENT','REFERENCE','OTHER')),
    sheet_count          INTEGER NOT NULL DEFAULT 0 CHECK (sheet_count >= 0),
    imported_by          TEXT REFERENCES users(id) ON DELETE RESTRICT,
    imported_at_utc      TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    notes                TEXT
);

CREATE TABLE IF NOT EXISTS source_row_refs (
    id                   TEXT PRIMARY KEY,
    source_sha256        TEXT NOT NULL REFERENCES source_file_catalog(source_sha256) ON DELETE RESTRICT,
    source_sheet_name    TEXT NOT NULL,
    source_row_number    INTEGER NOT NULL CHECK (source_row_number > 0),
    source_row_sha256    TEXT NOT NULL CHECK (length(source_row_sha256) = 64),
    entity_type          TEXT NOT NULL,
    entity_id            TEXT,
    external_key         TEXT,
    UNIQUE (source_sha256, source_sheet_name, source_row_number, entity_type)
);

CREATE TABLE IF NOT EXISTS party_aliases (
    id                   TEXT PRIMARY KEY,
    alias_code           TEXT NOT NULL UNIQUE,
    display_name         TEXT NOT NULL,
    alias_kind           TEXT NOT NULL DEFAULT 'UNKNOWN'
                         CHECK (alias_kind IN ('PERSON_NAME','COMPANY_NAME','BANK_LABEL','INCOMPLETE_NAME','UNKNOWN')),
    bank_name            TEXT,
    verification_status  TEXT NOT NULL DEFAULT 'NEEDS_REVIEW'
                         CHECK (verification_status IN ('SOURCE_MATCHED','NEEDS_REVIEW','MAPPED','REJECTED','INACTIVE')),
    party_id             TEXT REFERENCES parties(id) ON DELETE RESTRICT,
    source_sha256        TEXT REFERENCES source_file_catalog(source_sha256) ON DELETE RESTRICT,
    source_sheet_name    TEXT,
    source_row_number    INTEGER,
    source_row_sha256    TEXT,
    notes                TEXT,
    is_active            INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0,1)),
    created_at_utc       TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    CHECK (source_row_number IS NULL OR source_row_number > 0)
);

CREATE INDEX IF NOT EXISTS ix_party_aliases_display_name ON party_aliases(display_name);
CREATE INDEX IF NOT EXISTS ix_party_aliases_party_id ON party_aliases(party_id);

CREATE TABLE IF NOT EXISTS partner_statements (
    id                   TEXT PRIMARY KEY,
    source_sha256        TEXT NOT NULL REFERENCES source_file_catalog(source_sha256) ON DELETE RESTRICT,
    source_sheet_name    TEXT NOT NULL,
    source_partner_label TEXT NOT NULL,
    party_account_id     TEXT REFERENCES party_accounts(id) ON DELETE RESTRICT,
    statement_period_start_iso TEXT,
    statement_period_end_iso   TEXT,
    reported_total_irr   INTEGER,
    reported_paid_irr    INTEGER,
    status               TEXT NOT NULL DEFAULT 'REVIEW'
                         CHECK (status IN ('DRAFT','REVIEW','APPROVED','RECONCILED','VOIDED')),
    created_by           TEXT REFERENCES users(id) ON DELETE RESTRICT,
    created_at_utc       TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    notes                TEXT,
    UNIQUE (source_sha256, source_sheet_name)
);

CREATE TABLE IF NOT EXISTS partner_statement_lines (
    id                   TEXT PRIMARY KEY,
    statement_id         TEXT NOT NULL REFERENCES partner_statements(id) ON DELETE RESTRICT,
    line_type            TEXT NOT NULL CHECK (line_type IN ('SHIPMENT','PAYMENT','ADJUSTMENT','OTHER')),
    source_row_number    INTEGER NOT NULL CHECK (source_row_number > 0),
    date_jalali          TEXT,
    date_iso             TEXT,
    amount_irr           INTEGER CHECK (amount_irr IS NULL OR amount_irr >= 0),
    weight_sent_g        INTEGER CHECK (weight_sent_g IS NULL OR weight_sent_g >= 0),
    weight_offered_g     INTEGER CHECK (weight_offered_g IS NULL OR weight_offered_g >= 0),
    weight_difference_g  INTEGER,
    bank_name            TEXT,
    source_reference     TEXT,
    source_row_sha256    TEXT NOT NULL CHECK (length(source_row_sha256) = 64),
    validation_status    TEXT NOT NULL DEFAULT 'REVIEW'
                         CHECK (validation_status IN ('REVIEW','REVIEW_WITH_ISSUES','APPROVED','EXCLUDED')),
    notes                TEXT,
    UNIQUE (statement_id, source_row_number, line_type)
);

CREATE INDEX IF NOT EXISTS ix_partner_statement_lines_date ON partner_statement_lines(date_iso);
CREATE INDEX IF NOT EXISTS ix_partner_statement_lines_type ON partner_statement_lines(line_type);

CREATE TABLE IF NOT EXISTS reconciliation_cases (
    id                   TEXT PRIMARY KEY,
    issue_code           TEXT NOT NULL,
    severity             TEXT NOT NULL CHECK (severity IN ('INFO','LOW','MEDIUM','HIGH','CRITICAL')),
    source_sha256        TEXT REFERENCES source_file_catalog(source_sha256) ON DELETE RESTRICT,
    source_sheet_name    TEXT,
    source_row_number    TEXT,
    related_entity_type  TEXT,
    related_entity_id    TEXT,
    description          TEXT NOT NULL,
    recommended_action  TEXT NOT NULL,
    resolution_status    TEXT NOT NULL DEFAULT 'OPEN'
                         CHECK (resolution_status IN ('OPEN','IN_PROGRESS','RESOLVED','ACCEPTED_WITH_NOTE','REJECTED')),
    resolution_note      TEXT,
    resolved_by          TEXT REFERENCES users(id) ON DELETE RESTRICT,
    resolved_at_utc      TEXT,
    created_at_utc       TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);

CREATE INDEX IF NOT EXISTS ix_reconciliation_cases_status ON reconciliation_cases(resolution_status, severity);

INSERT OR IGNORE INTO schema_migrations(version, name)
VALUES (2, 'Source provenance, party aliases, partner statements and reconciliation cases');
UPDATE app_metadata SET meta_value='2', updated_at_utc=strftime('%Y-%m-%dT%H:%M:%fZ','now')
WHERE meta_key='schema_version';
PRAGMA user_version = 2;

COMMIT;
