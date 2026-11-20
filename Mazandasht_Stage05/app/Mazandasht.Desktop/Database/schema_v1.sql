-- MAZANDASHT LOCAL DATABASE SCHEMA
-- Stage 01 / schema version 1
-- No business/sample records are inserted by this file.
-- Target: SQLite 3.x, foreign keys enabled.

PRAGMA foreign_keys = ON;
PRAGMA journal_mode = WAL;
PRAGMA synchronous = FULL;
PRAGMA busy_timeout = 5000;

BEGIN IMMEDIATE;

CREATE TABLE IF NOT EXISTS schema_migrations (
    version             INTEGER PRIMARY KEY,
    name                TEXT NOT NULL,
    applied_at_utc      TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);

CREATE TABLE IF NOT EXISTS app_metadata (
    meta_key            TEXT PRIMARY KEY,
    meta_value          TEXT NOT NULL,
    updated_at_utc      TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);

CREATE TABLE IF NOT EXISTS app_settings (
    setting_key         TEXT PRIMARY KEY,
    value_json          TEXT NOT NULL,
    category            TEXT NOT NULL DEFAULT 'GENERAL',
    updated_by          TEXT,
    updated_at_utc      TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);

-- ---------------------------------------------------------------------------
-- SECURITY / USERS
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS roles (
    id                  TEXT PRIMARY KEY,
    role_code           TEXT NOT NULL UNIQUE,
    display_name        TEXT NOT NULL,
    is_system_role      INTEGER NOT NULL DEFAULT 1 CHECK (is_system_role IN (0,1)),
    is_active           INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0,1))
);

CREATE TABLE IF NOT EXISTS permissions (
    permission_code     TEXT PRIMARY KEY,
    display_name        TEXT NOT NULL,
    module_code         TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS role_permissions (
    role_id             TEXT NOT NULL REFERENCES roles(id) ON DELETE RESTRICT,
    permission_code     TEXT NOT NULL REFERENCES permissions(permission_code) ON DELETE RESTRICT,
    granted             INTEGER NOT NULL DEFAULT 1 CHECK (granted IN (0,1)),
    PRIMARY KEY (role_id, permission_code)
);

CREATE TABLE IF NOT EXISTS users (
    id                  TEXT PRIMARY KEY,
    username            TEXT NOT NULL COLLATE NOCASE UNIQUE,
    display_name        TEXT NOT NULL,
    password_algorithm  TEXT NOT NULL DEFAULT 'PBKDF2-HMAC-SHA256',
    password_hash       BLOB NOT NULL,
    password_salt       BLOB NOT NULL,
    password_iterations INTEGER NOT NULL CHECK (password_iterations >= 100000),
    role_id             TEXT NOT NULL REFERENCES roles(id) ON DELETE RESTRICT,
    phone               TEXT,
    is_active           INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0,1)),
    must_change_password INTEGER NOT NULL DEFAULT 1 CHECK (must_change_password IN (0,1)),
    last_login_at_utc   TEXT,
    created_at_utc      TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    created_by          TEXT REFERENCES users(id) ON DELETE RESTRICT
);

CREATE TABLE IF NOT EXISTS user_keys (
    id                  TEXT PRIMARY KEY,
    user_id             TEXT NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
    key_id              TEXT NOT NULL UNIQUE,
    secret_hash_sha256  TEXT NOT NULL CHECK (length(secret_hash_sha256) = 64),
    label               TEXT NOT NULL,
    created_at_utc      TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    expires_at_utc      TEXT,
    revoked_at_utc      TEXT,
    created_by          TEXT REFERENCES users(id) ON DELETE RESTRICT
);
CREATE INDEX IF NOT EXISTS ix_user_keys_user_active ON user_keys(user_id, revoked_at_utc, expires_at_utc);

CREATE TABLE IF NOT EXISTS user_sessions (
    id                  TEXT PRIMARY KEY,
    user_id             TEXT NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
    started_at_utc      TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    ended_at_utc        TEXT,
    session_status      TEXT NOT NULL DEFAULT 'ACTIVE' CHECK (session_status IN ('ACTIVE','CLOSED','INTERRUPTED')),
    machine_name        TEXT,
    app_version         TEXT,
    close_reason        TEXT
);
CREATE INDEX IF NOT EXISTS ix_user_sessions_user_time ON user_sessions(user_id, started_at_utc);

CREATE TABLE IF NOT EXISTS security_events (
    id                  TEXT PRIMARY KEY,
    user_id             TEXT REFERENCES users(id) ON DELETE RESTRICT,
    username_attempted  TEXT,
    event_type          TEXT NOT NULL CHECK (event_type IN ('LOGIN_SUCCESS','LOGIN_FAILURE','KEY_FAILURE','KEY_CREATED','KEY_REVOKED','PASSWORD_CHANGED','USER_DISABLED','SESSION_CLOSED','ADMIN_BOOTSTRAP')),
    occurred_at_utc     TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    machine_name        TEXT,
    details_json        TEXT
);
CREATE INDEX IF NOT EXISTS ix_security_events_time ON security_events(occurred_at_utc);

CREATE TABLE IF NOT EXISTS audit_log (
    id                  TEXT PRIMARY KEY,
    occurred_at_utc     TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    actor_user_id       TEXT REFERENCES users(id) ON DELETE RESTRICT,
    action_code         TEXT NOT NULL,
    entity_type         TEXT NOT NULL,
    entity_id           TEXT,
    before_json         TEXT,
    after_json          TEXT,
    machine_name        TEXT,
    correlation_id      TEXT
);
CREATE INDEX IF NOT EXISTS ix_audit_log_time ON audit_log(occurred_at_utc);
CREATE INDEX IF NOT EXISTS ix_audit_log_entity ON audit_log(entity_type, entity_id);

-- ---------------------------------------------------------------------------
-- MASTER DATA / CHART OF ACCOUNTS
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS account_natures (
    id                  TEXT PRIMARY KEY,
    nature_code         TEXT NOT NULL UNIQUE,
    display_name        TEXT NOT NULL,
    code_prefix         TEXT,
    is_active           INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0,1))
);

CREATE TABLE IF NOT EXISTS account_subnatures (
    id                  TEXT PRIMARY KEY,
    nature_id           TEXT NOT NULL REFERENCES account_natures(id) ON DELETE RESTRICT,
    display_name        TEXT NOT NULL,
    accounting_effect   TEXT NOT NULL CHECK (accounting_effect IN ('ASSET','LIABILITY','EQUITY','REVENUE','EXPENSE','CONTROL')),
    is_active           INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0,1)),
    UNIQUE (nature_id, display_name)
);

CREATE TABLE IF NOT EXISTS accounting_groups (
    id                  TEXT PRIMARY KEY,
    group_code          TEXT UNIQUE,
    display_name        TEXT NOT NULL UNIQUE,
    is_active           INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0,1))
);

CREATE TABLE IF NOT EXISTS audit_groups (
    id                  TEXT PRIMARY KEY,
    group_code          TEXT UNIQUE,
    display_name        TEXT NOT NULL UNIQUE,
    is_active           INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0,1))
);

CREATE TABLE IF NOT EXISTS parties (
    id                  TEXT PRIMARY KEY,
    party_kind          TEXT NOT NULL DEFAULT 'OTHER' CHECK (party_kind IN ('PERSON','COMPANY','GOVERNMENT','OTHER')),
    display_name        TEXT NOT NULL,
    national_id_enc     BLOB,
    national_id_lookup_hmac TEXT UNIQUE,
    phone_enc           BLOB,
    mobile_enc          BLOB,
    mobile_lookup_hmac  TEXT,
    address_enc         BLOB,
    notes               TEXT,
    is_active           INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0,1)),
    created_at_utc      TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    updated_at_utc      TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);
CREATE INDEX IF NOT EXISTS ix_parties_name ON parties(display_name);
CREATE INDEX IF NOT EXISTS ix_parties_mobile_hash ON parties(mobile_lookup_hmac);

CREATE TABLE IF NOT EXISTS ledger_accounts (
    id                  TEXT PRIMARY KEY,
    account_code        TEXT NOT NULL UNIQUE,
    display_name        TEXT NOT NULL,
    account_class       TEXT NOT NULL CHECK (account_class IN ('ASSET','LIABILITY','EQUITY','REVENUE','EXPENSE','CONTROL')),
    normal_side         TEXT NOT NULL CHECK (normal_side IN ('DEBIT','CREDIT')),
    parent_account_id   TEXT REFERENCES ledger_accounts(id) ON DELETE RESTRICT,
    is_system_account   INTEGER NOT NULL DEFAULT 0 CHECK (is_system_account IN (0,1)),
    is_active           INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0,1)),
    created_at_utc      TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    updated_at_utc      TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);
CREATE INDEX IF NOT EXISTS ix_ledger_accounts_class ON ledger_accounts(account_class, is_active);

CREATE TABLE IF NOT EXISTS party_accounts (
    id                  TEXT PRIMARY KEY,
    party_id            TEXT NOT NULL REFERENCES parties(id) ON DELETE RESTRICT,
    ledger_account_id   TEXT NOT NULL UNIQUE REFERENCES ledger_accounts(id) ON DELETE RESTRICT,
    nature_id           TEXT REFERENCES account_natures(id) ON DELETE RESTRICT,
    subnature_id        TEXT REFERENCES account_subnatures(id) ON DELETE RESTRICT,
    accounting_group_id TEXT REFERENCES accounting_groups(id) ON DELETE RESTRICT,
    audit_group_id      TEXT REFERENCES audit_groups(id) ON DELETE RESTRICT,
    is_active           INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0,1)),
    notes               TEXT
);
CREATE INDEX IF NOT EXISTS ix_party_accounts_party ON party_accounts(party_id);

CREATE TABLE IF NOT EXISTS bank_accounts (
    id                  TEXT PRIMARY KEY,
    ledger_account_id   TEXT NOT NULL UNIQUE REFERENCES ledger_accounts(id) ON DELETE RESTRICT,
    display_title       TEXT NOT NULL,
    bank_name           TEXT,
    branch_name         TEXT,
    account_number_enc  BLOB,
    iban_enc            BLOB,
    owner_name          TEXT,
    is_active           INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0,1)),
    notes               TEXT
);

CREATE TABLE IF NOT EXISTS main_groups (
    id                  TEXT PRIMARY KEY,
    group_code          TEXT NOT NULL UNIQUE,
    display_name        TEXT NOT NULL UNIQUE,
    notes               TEXT,
    is_active           INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0,1))
);

CREATE TABLE IF NOT EXISTS subgroups (
    id                  TEXT PRIMARY KEY,
    subgroup_code       TEXT NOT NULL UNIQUE,
    main_group_id       TEXT NOT NULL REFERENCES main_groups(id) ON DELETE RESTRICT,
    display_name        TEXT NOT NULL,
    ledger_account_id   TEXT REFERENCES ledger_accounts(id) ON DELETE RESTRICT,
    notes               TEXT,
    is_active           INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0,1)),
    UNIQUE (main_group_id, display_name)
);

CREATE TABLE IF NOT EXISTS transaction_types (
    id                  TEXT PRIMARY KEY,
    type_code           TEXT NOT NULL UNIQUE,
    display_name        TEXT NOT NULL UNIQUE,
    balance_effect      TEXT NOT NULL DEFAULT 'BY_DIRECTION' CHECK (balance_effect IN ('INCREASE','DECREASE','NEUTRAL','BY_DIRECTION','CUSTOM')),
    notes               TEXT,
    is_active           INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0,1))
);

CREATE TABLE IF NOT EXISTS operations (
    id                  TEXT PRIMARY KEY,
    operation_code      TEXT NOT NULL UNIQUE,
    display_name        TEXT NOT NULL UNIQUE,
    direction           TEXT NOT NULL CHECK (direction IN ('IN','OUT','NEUTRAL')),
    main_group_id       TEXT REFERENCES main_groups(id) ON DELETE RESTRICT,
    notes               TEXT,
    is_active           INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0,1))
);

CREATE TABLE IF NOT EXISTS units (
    id                  TEXT PRIMARY KEY,
    unit_code           TEXT NOT NULL UNIQUE,
    display_name        TEXT NOT NULL UNIQUE,
    base_unit_code      TEXT,
    conversion_numerator INTEGER NOT NULL DEFAULT 1 CHECK (conversion_numerator > 0),
    conversion_denominator INTEGER NOT NULL DEFAULT 1 CHECK (conversion_denominator > 0),
    is_active           INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0,1))
);

CREATE TABLE IF NOT EXISTS packaging_types (
    id                  TEXT PRIMARY KEY,
    packaging_code      TEXT NOT NULL UNIQUE,
    display_name        TEXT NOT NULL UNIQUE,
    capacity_g          INTEGER CHECK (capacity_g IS NULL OR capacity_g >= 0),
    tare_weight_g       INTEGER NOT NULL DEFAULT 0 CHECK (tare_weight_g >= 0),
    packaging_kind      TEXT NOT NULL DEFAULT 'OTHER' CHECK (packaging_kind IN ('BASKET','BOX','CARTON','BAG','BULK','OTHER')),
    notes               TEXT,
    is_active           INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0,1))
);

CREATE TABLE IF NOT EXISTS product_groups (
    id                  TEXT PRIMARY KEY,
    group_code          TEXT NOT NULL UNIQUE,
    parent_group_id     TEXT REFERENCES product_groups(id) ON DELETE RESTRICT,
    display_name        TEXT NOT NULL,
    sort_order          INTEGER NOT NULL DEFAULT 0,
    is_active           INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0,1)),
    UNIQUE (parent_group_id, display_name)
);

CREATE TABLE IF NOT EXISTS products (
    id                  TEXT PRIMARY KEY,
    product_code        TEXT NOT NULL UNIQUE,
    product_group_id    TEXT REFERENCES product_groups(id) ON DELETE RESTRICT,
    display_name        TEXT NOT NULL,
    product_role        TEXT NOT NULL DEFAULT 'BOTH' CHECK (product_role IN ('BUY','SELL','BOTH')),
    unit_id             TEXT REFERENCES units(id) ON DELETE RESTRICT,
    default_packaging_id TEXT REFERENCES packaging_types(id) ON DELETE RESTRICT,
    notes               TEXT,
    is_active           INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0,1))
);
CREATE INDEX IF NOT EXISTS ix_products_group ON products(product_group_id, is_active);

CREATE TABLE IF NOT EXISTS product_grades (
    id                  TEXT PRIMARY KEY,
    product_id          TEXT NOT NULL REFERENCES products(id) ON DELETE RESTRICT,
    grade_code          TEXT NOT NULL UNIQUE,
    display_name        TEXT NOT NULL,
    sort_order          INTEGER NOT NULL DEFAULT 0,
    is_active           INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0,1)),
    UNIQUE (product_id, display_name)
);

CREATE TABLE IF NOT EXISTS product_packaging (
    product_id          TEXT NOT NULL REFERENCES products(id) ON DELETE RESTRICT,
    packaging_type_id   TEXT NOT NULL REFERENCES packaging_types(id) ON DELETE RESTRICT,
    is_default          INTEGER NOT NULL DEFAULT 0 CHECK (is_default IN (0,1)),
    PRIMARY KEY (product_id, packaging_type_id)
);

CREATE TABLE IF NOT EXISTS warehouses (
    id                  TEXT PRIMARY KEY,
    warehouse_code      TEXT NOT NULL UNIQUE,
    display_name        TEXT NOT NULL UNIQUE,
    location_text       TEXT,
    keeper_party_account_id TEXT REFERENCES party_accounts(id) ON DELETE RESTRICT,
    notes               TEXT,
    is_active           INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0,1))
);

CREATE TABLE IF NOT EXISTS work_groups (
    id                  TEXT PRIMARY KEY,
    work_group_code     TEXT NOT NULL UNIQUE,
    display_name        TEXT NOT NULL UNIQUE,
    notes               TEXT,
    is_active           INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0,1))
);

CREATE TABLE IF NOT EXISTS employees (
    id                  TEXT PRIMARY KEY,
    party_account_id    TEXT NOT NULL UNIQUE REFERENCES party_accounts(id) ON DELETE RESTRICT,
    work_group_id       TEXT REFERENCES work_groups(id) ON DELETE RESTRICT,
    job_title           TEXT,
    payment_destination_enc BLOB,
    notes               TEXT,
    is_active           INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0,1))
);

CREATE TABLE IF NOT EXISTS operational_parameters (
    id                  TEXT PRIMARY KEY,
    parameter_code      TEXT NOT NULL UNIQUE,
    display_name        TEXT NOT NULL,
    value_integer      INTEGER,
    value_text          TEXT,
    value_kind          TEXT NOT NULL CHECK (value_kind IN ('INTEGER','TEXT','BOOLEAN')),
    unit_code           TEXT,
    effective_from_iso TEXT,
    effective_to_iso   TEXT,
    notes               TEXT,
    is_active           INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0,1)),
    CHECK (value_integer IS NOT NULL OR value_text IS NOT NULL)
);

-- ---------------------------------------------------------------------------
-- DOCUMENTS / OPERATIONS
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS documents (
    id                  TEXT PRIMARY KEY,
    document_type       TEXT NOT NULL CHECK (document_type IN ('OPENING_BALANCE','WEIGHING','PURCHASE','SALE','SORTING','STOCK_TRANSFER','BANK_TXN','CHEQUE','EXPENSE','WAGE','BASKET','ADJUSTMENT','OTHER')),
    fiscal_year         TEXT NOT NULL,
    document_number     TEXT NOT NULL,
    date_iso            TEXT NOT NULL CHECK (length(date_iso) = 10 AND substr(date_iso,5,1) = '-' AND substr(date_iso,8,1) = '-'),
    date_jalali         TEXT,
    time_local          TEXT NOT NULL DEFAULT '00:00:00',
    status              TEXT NOT NULL DEFAULT 'REVIEW' CHECK (status IN ('DRAFT','REVIEW','APPROVED','VOIDED')),
    requires_journal    INTEGER NOT NULL DEFAULT 0 CHECK (requires_journal IN (0,1)),
    requires_stock_posting INTEGER NOT NULL DEFAULT 0 CHECK (requires_stock_posting IN (0,1)),
    party_account_id    TEXT REFERENCES party_accounts(id) ON DELETE RESTRICT,
    reference_number    TEXT,
    notes               TEXT,
    created_by          TEXT NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
    created_at_utc      TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    updated_at_utc      TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    approved_by         TEXT REFERENCES users(id) ON DELETE RESTRICT,
    approved_at_utc     TEXT,
    voided_by           TEXT REFERENCES users(id) ON DELETE RESTRICT,
    voided_at_utc       TEXT,
    void_reason         TEXT,
    version_no          INTEGER NOT NULL DEFAULT 1 CHECK (version_no > 0),
    UNIQUE (document_type, fiscal_year, document_number)
);
CREATE INDEX IF NOT EXISTS ix_documents_date_type ON documents(date_iso, document_type, status);
CREATE INDEX IF NOT EXISTS ix_documents_party ON documents(party_account_id, date_iso);
CREATE INDEX IF NOT EXISTS ix_documents_status ON documents(status, created_at_utc);

CREATE TABLE IF NOT EXISTS document_lines (
    id                  TEXT PRIMARY KEY,
    document_id         TEXT NOT NULL REFERENCES documents(id) ON DELETE RESTRICT,
    line_number         INTEGER NOT NULL CHECK (line_number > 0),
    product_id          TEXT REFERENCES products(id) ON DELETE RESTRICT,
    grade_id            TEXT REFERENCES product_grades(id) ON DELETE RESTRICT,
    packaging_type_id   TEXT REFERENCES packaging_types(id) ON DELETE RESTRICT,
    quantity_g          INTEGER CHECK (quantity_g IS NULL OR quantity_g >= 0),
    quantity_count      INTEGER CHECK (quantity_count IS NULL OR quantity_count >= 0),
    unit_price_irr_per_kg INTEGER CHECK (unit_price_irr_per_kg IS NULL OR unit_price_irr_per_kg >= 0),
    amount_irr          INTEGER NOT NULL DEFAULT 0 CHECK (amount_irr >= 0),
    description         TEXT,
    UNIQUE (document_id, line_number)
);

CREATE TABLE IF NOT EXISTS document_relations (
    id                  TEXT PRIMARY KEY,
    source_document_id  TEXT NOT NULL REFERENCES documents(id) ON DELETE RESTRICT,
    target_document_id  TEXT NOT NULL REFERENCES documents(id) ON DELETE RESTRICT,
    relation_type       TEXT NOT NULL CHECK (relation_type IN ('WEIGH_FOR','INVOICE_FOR','PAYMENT_FOR','RETURN_OF','REVERSAL_OF','TRANSFER_PAIR','SOURCE_OF','OTHER')),
    notes               TEXT,
    CHECK (source_document_id <> target_document_id),
    UNIQUE (source_document_id, target_document_id, relation_type)
);

CREATE TABLE IF NOT EXISTS document_status_history (
    id                  TEXT PRIMARY KEY,
    document_id         TEXT NOT NULL REFERENCES documents(id) ON DELETE RESTRICT,
    old_status          TEXT,
    new_status          TEXT NOT NULL CHECK (new_status IN ('DRAFT','REVIEW','APPROVED','VOIDED')),
    changed_by          TEXT NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
    changed_at_utc      TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    reason              TEXT
);

-- Weighbridge: store exact integer grams; Excel kg values are converted during import.
CREATE TABLE IF NOT EXISTS weigh_tickets (
    document_id         TEXT PRIMARY KEY REFERENCES documents(id) ON DELETE RESTRICT,
    operation_id        TEXT NOT NULL REFERENCES operations(id) ON DELETE RESTRICT,
    direction           TEXT NOT NULL CHECK (direction IN ('IN','OUT')),
    party_account_id    TEXT REFERENCES party_accounts(id) ON DELETE RESTRICT,
    product_id          TEXT REFERENCES products(id) ON DELETE RESTRICT,
    grade_id            TEXT REFERENCES product_grades(id) ON DELETE RESTRICT,
    gross_weight_g      INTEGER NOT NULL CHECK (gross_weight_g > 0),
    basket_count        INTEGER NOT NULL DEFAULT 0 CHECK (basket_count >= 0),
    tare_weight_g       INTEGER NOT NULL DEFAULT 0 CHECK (tare_weight_g >= 0),
    loss_rate_bps       INTEGER NOT NULL DEFAULT 0 CHECK (loss_rate_bps BETWEEN 0 AND 10000),
    loss_weight_g       INTEGER NOT NULL DEFAULT 0 CHECK (loss_weight_g >= 0),
    net_weight_g        INTEGER NOT NULL CHECK (net_weight_g >= 0),
    amount_irr          INTEGER NOT NULL DEFAULT 0 CHECK (amount_irr >= 0),
    warehouse_id        TEXT REFERENCES warehouses(id) ON DELETE RESTRICT,
    scale_ticket_number TEXT,
    calculation_snapshot_json TEXT,
    notes               TEXT,
    CHECK (tare_weight_g + loss_weight_g <= gross_weight_g)
);
CREATE INDEX IF NOT EXISTS ix_weigh_tickets_party ON weigh_tickets(party_account_id);
CREATE INDEX IF NOT EXISTS ix_weigh_tickets_operation ON weigh_tickets(operation_id, direction);

CREATE TABLE IF NOT EXISTS weigh_ticket_packaging (
    id                  TEXT PRIMARY KEY,
    document_id         TEXT NOT NULL REFERENCES weigh_tickets(document_id) ON DELETE RESTRICT,
    packaging_type_id   TEXT NOT NULL REFERENCES packaging_types(id) ON DELETE RESTRICT,
    package_count       INTEGER NOT NULL CHECK (package_count > 0),
    tare_weight_g_snapshot INTEGER NOT NULL CHECK (tare_weight_g_snapshot >= 0),
    UNIQUE (document_id, packaging_type_id)
);

-- Stock lots and movement ledger: movements are the source of stock balances.
CREATE TABLE IF NOT EXISTS stock_lots (
    id                  TEXT PRIMARY KEY,
    lot_code            TEXT NOT NULL UNIQUE,
    product_id          TEXT NOT NULL REFERENCES products(id) ON DELETE RESTRICT,
    grade_id            TEXT REFERENCES product_grades(id) ON DELETE RESTRICT,
    packaging_type_id   TEXT REFERENCES packaging_types(id) ON DELETE RESTRICT,
    origin_document_id  TEXT NOT NULL REFERENCES documents(id) ON DELETE RESTRICT,
    initial_quantity_g  INTEGER NOT NULL CHECK (initial_quantity_g >= 0),
    created_at_utc      TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    notes               TEXT
);

CREATE TABLE IF NOT EXISTS stock_movements (
    id                  TEXT PRIMARY KEY,
    movement_group_id   TEXT NOT NULL,
    stock_lot_id        TEXT NOT NULL REFERENCES stock_lots(id) ON DELETE RESTRICT,
    warehouse_id        TEXT NOT NULL REFERENCES warehouses(id) ON DELETE RESTRICT,
    source_document_id  TEXT NOT NULL REFERENCES documents(id) ON DELETE RESTRICT,
    movement_type       TEXT NOT NULL CHECK (movement_type IN ('RECEIPT','ISSUE','SORTING_INPUT','SORTING_OUTPUT','TRANSFER_IN','TRANSFER_OUT','ADJUSTMENT','REVERSAL','WASTE')),
    quantity_delta_g    INTEGER NOT NULL CHECK (quantity_delta_g <> 0),
    is_posted           INTEGER NOT NULL DEFAULT 0 CHECK (is_posted IN (0,1)),
    reversal_of_movement_id TEXT UNIQUE REFERENCES stock_movements(id) ON DELETE RESTRICT,
    occurred_at_utc     TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    notes               TEXT
);
CREATE INDEX IF NOT EXISTS ix_stock_movements_lot_wh ON stock_movements(stock_lot_id, warehouse_id);
CREATE INDEX IF NOT EXISTS ix_stock_movements_doc ON stock_movements(source_document_id);

CREATE VIEW IF NOT EXISTS v_stock_balance AS
SELECT stock_lot_id, warehouse_id, SUM(quantity_delta_g) AS quantity_g
FROM stock_movements
WHERE is_posted = 1
GROUP BY stock_lot_id, warehouse_id
HAVING SUM(quantity_delta_g) <> 0;

CREATE TABLE IF NOT EXISTS stock_allocations (
    id                  TEXT PRIMARY KEY,
    document_id         TEXT NOT NULL REFERENCES documents(id) ON DELETE RESTRICT,
    document_line_id    TEXT REFERENCES document_lines(id) ON DELETE RESTRICT,
    stock_lot_id        TEXT NOT NULL REFERENCES stock_lots(id) ON DELETE RESTRICT,
    warehouse_id        TEXT NOT NULL REFERENCES warehouses(id) ON DELETE RESTRICT,
    quantity_g          INTEGER NOT NULL CHECK (quantity_g > 0),
    allocation_kind     TEXT NOT NULL CHECK (allocation_kind IN ('SALE','WEIGH_OUT','SORTING_INPUT','TRANSFER','OTHER')),
    UNIQUE (document_id, document_line_id, stock_lot_id, allocation_kind)
);

-- Sorting batches: inputs are consumed from lots; outputs create new lots.
CREATE TABLE IF NOT EXISTS sorting_batches (
    document_id         TEXT PRIMARY KEY REFERENCES documents(id) ON DELETE RESTRICT,
    input_weight_g      INTEGER NOT NULL CHECK (input_weight_g > 0),
    output_weight_g     INTEGER NOT NULL DEFAULT 0 CHECK (output_weight_g >= 0),
    waste_weight_g      INTEGER NOT NULL DEFAULT 0 CHECK (waste_weight_g >= 0),
    process_loss_weight_g INTEGER NOT NULL DEFAULT 0 CHECK (process_loss_weight_g >= 0),
    variance_weight_g   INTEGER NOT NULL DEFAULT 0,
    variance_tolerance_g INTEGER NOT NULL DEFAULT 0 CHECK (variance_tolerance_g >= 0),
    reconciliation_status TEXT NOT NULL DEFAULT 'PENDING' CHECK (reconciliation_status IN ('PENDING','MATCHED','WARNING','REJECTED')),
    notes               TEXT
);

CREATE TABLE IF NOT EXISTS sorting_batch_inputs (
    id                  TEXT PRIMARY KEY,
    sorting_document_id TEXT NOT NULL REFERENCES sorting_batches(document_id) ON DELETE RESTRICT,
    stock_lot_id        TEXT NOT NULL REFERENCES stock_lots(id) ON DELETE RESTRICT,
    quantity_g          INTEGER NOT NULL CHECK (quantity_g > 0),
    UNIQUE (sorting_document_id, stock_lot_id)
);

CREATE TABLE IF NOT EXISTS sorting_outputs (
    id                  TEXT PRIMARY KEY,
    sorting_document_id TEXT NOT NULL REFERENCES sorting_batches(document_id) ON DELETE RESTRICT,
    product_id          TEXT NOT NULL REFERENCES products(id) ON DELETE RESTRICT,
    grade_id            TEXT REFERENCES product_grades(id) ON DELETE RESTRICT,
    packaging_type_id   TEXT REFERENCES packaging_types(id) ON DELETE RESTRICT,
    stock_lot_id        TEXT NOT NULL UNIQUE REFERENCES stock_lots(id) ON DELETE RESTRICT,
    quantity_g          INTEGER NOT NULL CHECK (quantity_g > 0),
    package_count       INTEGER CHECK (package_count IS NULL OR package_count >= 0),
    line_number         INTEGER NOT NULL CHECK (line_number > 0),
    UNIQUE (sorting_document_id, line_number)
);

-- Commercial invoices. Weigh tickets move stock; invoice lines settle commercial value.
CREATE TABLE IF NOT EXISTS purchase_invoice_lines (
    id                  TEXT PRIMARY KEY,
    document_id         TEXT NOT NULL REFERENCES documents(id) ON DELETE RESTRICT,
    line_number         INTEGER NOT NULL CHECK (line_number > 0),
    source_weigh_document_id TEXT REFERENCES weigh_tickets(document_id) ON DELETE RESTRICT,
    product_id          TEXT REFERENCES products(id) ON DELETE RESTRICT,
    quantity_g          INTEGER NOT NULL CHECK (quantity_g >= 0),
    unit_price_irr_per_kg INTEGER NOT NULL DEFAULT 0 CHECK (unit_price_irr_per_kg >= 0),
    gross_amount_irr    INTEGER NOT NULL DEFAULT 0 CHECK (gross_amount_irr >= 0),
    discount_irr        INTEGER NOT NULL DEFAULT 0 CHECK (discount_irr >= 0),
    freight_irr         INTEGER NOT NULL DEFAULT 0 CHECK (freight_irr >= 0),
    commission_irr      INTEGER NOT NULL DEFAULT 0 CHECK (commission_irr >= 0),
    other_adjustments_irr INTEGER NOT NULL DEFAULT 0,
    net_amount_irr      INTEGER NOT NULL DEFAULT 0 CHECK (net_amount_irr >= 0),
    notes               TEXT,
    UNIQUE (document_id, line_number)
);

CREATE TABLE IF NOT EXISTS sales_invoice_lines (
    id                  TEXT PRIMARY KEY,
    document_id         TEXT NOT NULL REFERENCES documents(id) ON DELETE RESTRICT,
    line_number         INTEGER NOT NULL CHECK (line_number > 0),
    source_weigh_document_id TEXT REFERENCES weigh_tickets(document_id) ON DELETE RESTRICT,
    source_sorting_output_id TEXT REFERENCES sorting_outputs(id) ON DELETE RESTRICT,
    product_id          TEXT REFERENCES products(id) ON DELETE RESTRICT,
    grade_id            TEXT REFERENCES product_grades(id) ON DELETE RESTRICT,
    packaging_type_id   TEXT REFERENCES packaging_types(id) ON DELETE RESTRICT,
    quantity_g          INTEGER NOT NULL CHECK (quantity_g >= 0),
    unit_price_irr_per_kg INTEGER NOT NULL DEFAULT 0 CHECK (unit_price_irr_per_kg >= 0),
    gross_amount_irr    INTEGER NOT NULL DEFAULT 0 CHECK (gross_amount_irr >= 0),
    discount_irr        INTEGER NOT NULL DEFAULT 0 CHECK (discount_irr >= 0),
    freight_irr         INTEGER NOT NULL DEFAULT 0 CHECK (freight_irr >= 0),
    commission_irr      INTEGER NOT NULL DEFAULT 0 CHECK (commission_irr >= 0),
    other_costs_irr     INTEGER NOT NULL DEFAULT 0 CHECK (other_costs_irr >= 0),
    net_receivable_irr  INTEGER NOT NULL DEFAULT 0 CHECK (net_receivable_irr >= 0),
    notes               TEXT,
    UNIQUE (document_id, line_number)
);

-- Cheques and bank movements.
CREATE TABLE IF NOT EXISTS cheque_books (
    id                  TEXT PRIMARY KEY,
    bank_account_id     TEXT NOT NULL REFERENCES bank_accounts(id) ON DELETE RESTRICT,
    number_from         TEXT NOT NULL,
    number_to           TEXT NOT NULL,
    notes               TEXT,
    created_at_utc      TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);

CREATE TABLE IF NOT EXISTS cheques (
    id                  TEXT PRIMARY KEY,
    document_id         TEXT NOT NULL UNIQUE REFERENCES documents(id) ON DELETE RESTRICT,
    cheque_number       TEXT NOT NULL,
    bank_account_id     TEXT REFERENCES bank_accounts(id) ON DELETE RESTRICT,
    direction           TEXT NOT NULL CHECK (direction IN ('IN','OUT')),
    issue_date_iso      TEXT NOT NULL,
    due_date_iso        TEXT NOT NULL,
    amount_irr          INTEGER NOT NULL CHECK (amount_irr > 0),
    beneficiary_account_id TEXT REFERENCES party_accounts(id) ON DELETE RESTRICT,
    status              TEXT NOT NULL DEFAULT 'ISSUED' CHECK (status IN ('ISSUED','CIRCULATING','CLEARED','RETURNED','CANCELLED')),
    saiady_id_enc       BLOB,
    cheque_book_id      TEXT REFERENCES cheque_books(id) ON DELETE RESTRICT,
    notes               TEXT,
    UNIQUE (bank_account_id, cheque_number),
    CHECK (direction <> 'OUT' OR bank_account_id IS NOT NULL)
);

CREATE TABLE IF NOT EXISTS cheque_events (
    id                  TEXT PRIMARY KEY,
    cheque_id           TEXT NOT NULL REFERENCES cheques(id) ON DELETE RESTRICT,
    event_type          TEXT NOT NULL CHECK (event_type IN ('ISSUED','RECEIVED','DEPOSITED','CLEARED','RETURNED','CANCELLED','REVERSED')),
    event_date_iso      TEXT NOT NULL,
    amount_irr          INTEGER NOT NULL CHECK (amount_irr >= 0),
    related_document_id TEXT REFERENCES documents(id) ON DELETE RESTRICT,
    notes               TEXT,
    created_by          TEXT NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
    created_at_utc      TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);

CREATE TABLE IF NOT EXISTS payment_transactions (
    document_id         TEXT PRIMARY KEY REFERENCES documents(id) ON DELETE RESTRICT,
    bank_account_id     TEXT NOT NULL REFERENCES bank_accounts(id) ON DELETE RESTRICT,
    party_account_id    TEXT REFERENCES party_accounts(id) ON DELETE RESTRICT,
    direction           TEXT NOT NULL CHECK (direction IN ('IN','OUT')),
    transaction_type_id TEXT REFERENCES transaction_types(id) ON DELETE RESTRICT,
    main_group_id       TEXT REFERENCES main_groups(id) ON DELETE RESTRICT,
    subgroup_id         TEXT REFERENCES subgroups(id) ON DELETE RESTRICT,
    amount_irr          INTEGER NOT NULL CHECK (amount_irr > 0),
    statement_balance_irr INTEGER,
    system_balance_irr  INTEGER,
    cheque_id           TEXT REFERENCES cheques(id) ON DELETE RESTRICT,
    transfer_pair_id    TEXT,
    notes               TEXT
);
CREATE INDEX IF NOT EXISTS ix_payment_transactions_bank_date ON payment_transactions(bank_account_id, document_id);

-- Basket purchase, custody and transfers are separate physical movements.
CREATE TABLE IF NOT EXISTS basket_movements (
    document_id         TEXT PRIMARY KEY REFERENCES documents(id) ON DELETE RESTRICT,
    packaging_type_id   TEXT NOT NULL REFERENCES packaging_types(id) ON DELETE RESTRICT,
    movement_type       TEXT NOT NULL CHECK (movement_type IN ('PURCHASE','ISSUE','RETURN','CUSTODY_IN','CUSTODY_OUT','TRANSFER_IN','TRANSFER_OUT','ADJUSTMENT')),
    quantity            INTEGER NOT NULL CHECK (quantity > 0),
    from_party_account_id TEXT REFERENCES party_accounts(id) ON DELETE RESTRICT,
    to_party_account_id TEXT REFERENCES party_accounts(id) ON DELETE RESTRICT,
    from_warehouse_id  TEXT REFERENCES warehouses(id) ON DELETE RESTRICT,
    to_warehouse_id    TEXT REFERENCES warehouses(id) ON DELETE RESTRICT,
    unit_price_irr      INTEGER NOT NULL DEFAULT 0 CHECK (unit_price_irr >= 0),
    amount_irr          INTEGER NOT NULL DEFAULT 0 CHECK (amount_irr >= 0),
    notes               TEXT
);

-- Wages: group wage events must reconcile to employee allocations before approval.
CREATE TABLE IF NOT EXISTS wage_events (
    document_id         TEXT PRIMARY KEY REFERENCES documents(id) ON DELETE RESTRICT,
    wage_kind           TEXT NOT NULL CHECK (wage_kind IN ('INDIVIDUAL','GROUP')),
    employee_id         TEXT REFERENCES employees(id) ON DELETE RESTRICT,
    work_group_id       TEXT REFERENCES work_groups(id) ON DELETE RESTRICT,
    total_amount_irr    INTEGER NOT NULL CHECK (total_amount_irr > 0),
    CHECK ((wage_kind = 'INDIVIDUAL' AND employee_id IS NOT NULL AND work_group_id IS NULL)
        OR (wage_kind = 'GROUP' AND employee_id IS NULL AND work_group_id IS NOT NULL))
);

CREATE TABLE IF NOT EXISTS wage_allocations (
    id                  TEXT PRIMARY KEY,
    wage_document_id    TEXT NOT NULL REFERENCES wage_events(document_id) ON DELETE RESTRICT,
    employee_id         TEXT NOT NULL REFERENCES employees(id) ON DELETE RESTRICT,
    amount_irr          INTEGER NOT NULL CHECK (amount_irr > 0),
    notes               TEXT,
    UNIQUE (wage_document_id, employee_id)
);

-- Double-entry accounting journal. Amounts are integer Rials.
CREATE TABLE IF NOT EXISTS journal_entries (
    id                  TEXT PRIMARY KEY,
    journal_number      TEXT NOT NULL UNIQUE,
    document_id         TEXT NOT NULL UNIQUE REFERENCES documents(id) ON DELETE RESTRICT,
    entry_date_iso      TEXT NOT NULL,
    description         TEXT NOT NULL,
    is_posted           INTEGER NOT NULL DEFAULT 0 CHECK (is_posted IN (0,1)),
    posted_by           TEXT REFERENCES users(id) ON DELETE RESTRICT,
    posted_at_utc       TEXT,
    reversal_of_entry_id TEXT UNIQUE REFERENCES journal_entries(id) ON DELETE RESTRICT,
    created_at_utc      TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);

CREATE TABLE IF NOT EXISTS journal_lines (
    id                  TEXT PRIMARY KEY,
    journal_entry_id    TEXT NOT NULL REFERENCES journal_entries(id) ON DELETE RESTRICT,
    line_number         INTEGER NOT NULL CHECK (line_number > 0),
    ledger_account_id   TEXT NOT NULL REFERENCES ledger_accounts(id) ON DELETE RESTRICT,
    debit_irr           INTEGER NOT NULL DEFAULT 0 CHECK (debit_irr >= 0),
    credit_irr          INTEGER NOT NULL DEFAULT 0 CHECK (credit_irr >= 0),
    description         TEXT,
    CHECK ((debit_irr > 0 AND credit_irr = 0) OR (credit_irr > 0 AND debit_irr = 0)),
    UNIQUE (journal_entry_id, line_number)
);
CREATE INDEX IF NOT EXISTS ix_journal_lines_account ON journal_lines(ledger_account_id);

CREATE VIEW IF NOT EXISTS v_trial_balance AS
SELECT
    la.id AS ledger_account_id,
    la.account_code,
    la.display_name,
    la.account_class,
    COALESCE(j.total_debit_irr,0) AS total_debit_irr,
    COALESCE(j.total_credit_irr,0) AS total_credit_irr,
    COALESCE(j.total_debit_irr,0) - COALESCE(j.total_credit_irr,0) AS balance_debit_positive_irr
FROM ledger_accounts la
LEFT JOIN (
    SELECT jl.ledger_account_id,
           SUM(jl.debit_irr) AS total_debit_irr,
           SUM(jl.credit_irr) AS total_credit_irr
    FROM journal_lines jl
    INNER JOIN journal_entries je ON je.id = jl.journal_entry_id
    WHERE je.is_posted = 1
    GROUP BY jl.ledger_account_id
) j ON j.ledger_account_id = la.id;

-- ---------------------------------------------------------------------------
-- BACKUP / EXCEL IMPORT & EXPORT CONTROL
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS backup_profiles (
    id                  TEXT PRIMARY KEY,
    profile_name        TEXT NOT NULL UNIQUE,
    destination_path    TEXT NOT NULL,
    keep_last_n         INTEGER NOT NULL DEFAULT 30 CHECK (keep_last_n >= 1),
    keep_monthly_n      INTEGER NOT NULL DEFAULT 3 CHECK (keep_monthly_n >= 0),
    backup_on_logout    INTEGER NOT NULL DEFAULT 1 CHECK (backup_on_logout IN (0,1)),
    backup_on_exit      INTEGER NOT NULL DEFAULT 1 CHECK (backup_on_exit IN (0,1)),
    is_active           INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0,1)),
    created_by          TEXT REFERENCES users(id) ON DELETE RESTRICT
);

CREATE TABLE IF NOT EXISTS backup_runs (
    id                  TEXT PRIMARY KEY,
    profile_id          TEXT REFERENCES backup_profiles(id) ON DELETE RESTRICT,
    reason              TEXT NOT NULL CHECK (reason IN ('LOGOUT','APP_EXIT','MANUAL','PRE_MIGRATION','SCHEDULED','PRE_RESTORE')),
    file_path           TEXT NOT NULL,
    file_size_bytes     INTEGER,
    sha256              TEXT,
    status              TEXT NOT NULL CHECK (status IN ('STARTED','SUCCESS','FAILED','VERIFIED')),
    created_by          TEXT REFERENCES users(id) ON DELETE RESTRICT,
    started_at_utc      TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    completed_at_utc    TEXT,
    error_text          TEXT
);

CREATE TABLE IF NOT EXISTS import_batches (
    id                  TEXT PRIMARY KEY,
    source_file_name    TEXT NOT NULL,
    source_file_sha256  TEXT,
    source_sheet_name   TEXT NOT NULL,
    import_profile      TEXT NOT NULL,
    status              TEXT NOT NULL CHECK (status IN ('PREVIEW','VALIDATED','COMMITTED','CANCELLED','FAILED')),
    total_rows          INTEGER NOT NULL DEFAULT 0 CHECK (total_rows >= 0),
    accepted_rows       INTEGER NOT NULL DEFAULT 0 CHECK (accepted_rows >= 0),
    rejected_rows       INTEGER NOT NULL DEFAULT 0 CHECK (rejected_rows >= 0),
    created_by          TEXT NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
    created_at_utc      TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    committed_at_utc    TEXT
);

CREATE TABLE IF NOT EXISTS import_rows (
    id                  TEXT PRIMARY KEY,
    import_batch_id     TEXT NOT NULL REFERENCES import_batches(id) ON DELETE RESTRICT,
    source_row_number   INTEGER NOT NULL CHECK (source_row_number > 0),
    external_key        TEXT,
    row_status          TEXT NOT NULL CHECK (row_status IN ('PENDING','ACCEPTED','REJECTED','DUPLICATE','SAMPLE','IMPORTED')),
    target_entity       TEXT,
    target_id           TEXT,
    raw_row_json        TEXT NOT NULL,
    validation_errors_json TEXT,
    UNIQUE (import_batch_id, source_row_number)
);

CREATE TABLE IF NOT EXISTS export_runs (
    id                  TEXT PRIMARY KEY,
    export_kind         TEXT NOT NULL CHECK (export_kind IN ('FULL_EXCEL','DELTA_EXCEL','REPORT','BACKUP_SNAPSHOT')),
    target_path         TEXT NOT NULL,
    status              TEXT NOT NULL CHECK (status IN ('STARTED','SUCCESS','FAILED')),
    created_by          TEXT REFERENCES users(id) ON DELETE RESTRICT,
    created_at_utc      TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    completed_at_utc    TEXT,
    rows_exported       INTEGER NOT NULL DEFAULT 0 CHECK (rows_exported >= 0),
    error_text          TEXT
);

-- ---------------------------------------------------------------------------
-- INTEGRITY TRIGGERS
-- ---------------------------------------------------------------------------
CREATE TRIGGER IF NOT EXISTS trg_journal_must_balance_before_post
BEFORE UPDATE OF is_posted ON journal_entries
WHEN NEW.is_posted = 1 AND OLD.is_posted = 0
BEGIN
    SELECT CASE WHEN (SELECT status FROM documents WHERE id = NEW.document_id) <> 'APPROVED'
        THEN RAISE(ABORT, 'Journal can only be posted for an approved document') END;
    SELECT CASE WHEN (SELECT COUNT(*) FROM journal_lines WHERE journal_entry_id = NEW.id) < 2
        THEN RAISE(ABORT, 'Journal requires at least two lines') END;
    SELECT CASE WHEN (SELECT COALESCE(SUM(debit_irr),0) - COALESCE(SUM(credit_irr),0) FROM journal_lines WHERE journal_entry_id = NEW.id) <> 0
        THEN RAISE(ABORT, 'Journal is not balanced') END;
    SELECT CASE WHEN (SELECT COALESCE(SUM(debit_irr),0) FROM journal_lines WHERE journal_entry_id = NEW.id) = 0
        THEN RAISE(ABORT, 'Journal total must be greater than zero') END;
END;

CREATE TRIGGER IF NOT EXISTS trg_journal_line_no_insert_after_post
BEFORE INSERT ON journal_lines
WHEN (SELECT is_posted FROM journal_entries WHERE id = NEW.journal_entry_id) = 1
BEGIN
    SELECT RAISE(ABORT, 'Posted journal lines are immutable');
END;

CREATE TRIGGER IF NOT EXISTS trg_journal_line_no_update_after_post
BEFORE UPDATE ON journal_lines
WHEN (SELECT is_posted FROM journal_entries WHERE id = OLD.journal_entry_id) = 1
BEGIN
    SELECT RAISE(ABORT, 'Posted journal lines are immutable');
END;

CREATE TRIGGER IF NOT EXISTS trg_journal_line_no_delete_after_post
BEFORE DELETE ON journal_lines
WHEN (SELECT is_posted FROM journal_entries WHERE id = OLD.journal_entry_id) = 1
BEGIN
    SELECT RAISE(ABORT, 'Posted journal lines are immutable');
END;

CREATE TRIGGER IF NOT EXISTS trg_document_approval_requires_journal
BEFORE UPDATE OF status ON documents
WHEN NEW.status = 'APPROVED' AND OLD.status <> 'APPROVED' AND NEW.requires_journal = 1
BEGIN
    SELECT CASE WHEN NOT EXISTS (
        SELECT 1 FROM journal_entries je
        WHERE je.document_id = NEW.id
          AND (SELECT COUNT(*) FROM journal_lines jl WHERE jl.journal_entry_id = je.id) >= 2
          AND (SELECT COALESCE(SUM(debit_irr),0) - COALESCE(SUM(credit_irr),0) FROM journal_lines jl WHERE jl.journal_entry_id = je.id) = 0
          AND (SELECT COALESCE(SUM(debit_irr),0) FROM journal_lines jl WHERE jl.journal_entry_id = je.id) > 0
    ) THEN RAISE(ABORT, 'Document requires a balanced journal before approval') END;
END;

CREATE TRIGGER IF NOT EXISTS trg_document_approval_requires_stock
BEFORE UPDATE OF status ON documents
WHEN NEW.status = 'APPROVED' AND OLD.status <> 'APPROVED' AND NEW.requires_stock_posting = 1
BEGIN
    SELECT CASE WHEN NOT EXISTS (
        SELECT 1 FROM stock_movements sm WHERE sm.source_document_id = NEW.id AND sm.is_posted = 0
    ) THEN RAISE(ABORT, 'Document requires pending stock movements before approval') END;
END;

CREATE TRIGGER IF NOT EXISTS trg_stock_movement_post_requires_approved_document
BEFORE UPDATE OF is_posted ON stock_movements
WHEN NEW.is_posted = 1 AND OLD.is_posted = 0
BEGIN
    SELECT CASE WHEN (SELECT status FROM documents WHERE id = NEW.source_document_id) <> 'APPROVED'
        THEN RAISE(ABORT, 'Stock movement can only be posted for an approved document') END;
END;

CREATE TRIGGER IF NOT EXISTS trg_stock_movement_no_update_after_post
BEFORE UPDATE ON stock_movements
WHEN OLD.is_posted = 1
BEGIN
    SELECT RAISE(ABORT, 'Posted stock movements are immutable');
END;

CREATE TRIGGER IF NOT EXISTS trg_stock_movement_no_delete_after_post
BEFORE DELETE ON stock_movements
WHEN OLD.is_posted = 1
BEGIN
    SELECT RAISE(ABORT, 'Posted stock movements are immutable');
END;

CREATE TRIGGER IF NOT EXISTS trg_document_status_transition
BEFORE UPDATE OF status ON documents
WHEN NOT (
    OLD.status = NEW.status
    OR (OLD.status = 'DRAFT' AND NEW.status IN ('REVIEW','VOIDED'))
    OR (OLD.status = 'REVIEW' AND NEW.status IN ('APPROVED','VOIDED'))
    OR (OLD.status = 'APPROVED' AND NEW.status = 'VOIDED')
)
BEGIN
    SELECT RAISE(ABORT, 'Invalid document status transition');
END;

CREATE TRIGGER IF NOT EXISTS trg_approved_document_void_requires_reversal
BEFORE UPDATE OF status ON documents
WHEN OLD.status = 'APPROVED' AND NEW.status = 'VOIDED' AND OLD.requires_journal = 1
BEGIN
    SELECT CASE WHEN NOT EXISTS (
        SELECT 1
        FROM journal_entries original_entry
        JOIN journal_entries reversal_entry ON reversal_entry.reversal_of_entry_id = original_entry.id
        WHERE original_entry.document_id = OLD.id
          AND original_entry.is_posted = 1
          AND reversal_entry.is_posted = 1
    ) THEN RAISE(ABORT, 'Approved financial document requires a posted reversal journal before voiding') END;
END;

CREATE TRIGGER IF NOT EXISTS trg_approved_stock_document_void_requires_reversal
BEFORE UPDATE OF status ON documents
WHEN OLD.status = 'APPROVED' AND NEW.status = 'VOIDED' AND OLD.requires_stock_posting = 1
BEGIN
    SELECT CASE WHEN EXISTS (
        SELECT 1 FROM stock_movements original_movement
        WHERE original_movement.source_document_id = OLD.id
          AND original_movement.is_posted = 1
          AND NOT EXISTS (
              SELECT 1 FROM stock_movements reversal_movement
              WHERE reversal_movement.reversal_of_movement_id = original_movement.id
                AND reversal_movement.is_posted = 1
          )
    ) THEN RAISE(ABORT, 'Approved stock document requires posted reversal movements before voiding') END;
END;

CREATE TRIGGER IF NOT EXISTS trg_audit_log_no_update
BEFORE UPDATE ON audit_log
BEGIN
    SELECT RAISE(ABORT, 'Audit log is append-only');
END;

CREATE TRIGGER IF NOT EXISTS trg_audit_log_no_delete
BEFORE DELETE ON audit_log
BEGIN
    SELECT RAISE(ABORT, 'Audit log is append-only');
END;

INSERT OR IGNORE INTO schema_migrations(version, name) VALUES (1, 'Initial relational core schema');
INSERT OR IGNORE INTO app_metadata(meta_key, meta_value) VALUES ('application_code', 'MAZANDASHT');
INSERT OR IGNORE INTO app_metadata(meta_key, meta_value) VALUES ('schema_version', '1');

PRAGMA user_version = 1;
COMMIT;
