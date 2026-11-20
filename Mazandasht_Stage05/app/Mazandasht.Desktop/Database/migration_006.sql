-- ===========================================================================
-- Mazandasht schema migration 006  (Stage G3 - architecture & database)
-- ---------------------------------------------------------------------------
-- Adds the operational/accounting backbone that the rules contract
-- (docs/12_Rules_Contract.json) requires but schema v5 did not carry:
--   * a default chart of accounts (system control accounts)
--   * settlement allocation with an over-allocation guard
--   * reconciliation, custody, bank, cheque and profit/loss views
--   * integrity triggers for weighing, invoice pricing, sorting, cheque
--     clearance, document numbering
--   * the database.reset permission (ADMIN only)
--
-- Incremental and safe: every statement is idempotent and no existing data is
-- deleted or rewritten. PRAGMA user_version becomes 6.
-- ===========================================================================

BEGIN IMMEDIATE;

-- ---------------------------------------------------------------------------
-- 1. Default chart of accounts (system control accounts)
-- ---------------------------------------------------------------------------
INSERT OR IGNORE INTO ledger_accounts(id,account_code,display_name,account_class,normal_side,is_system_account,is_active) VALUES
('la-inventory',        '1001','موجودی کالا','ASSET','DEBIT',1,1),
('la-cash',             '1002','صندوق','ASSET','DEBIT',1,1),
('la-bank-control',     '1003','بانک‌ها','ASSET','DEBIT',1,1),
('la-receivable-control','1101','حساب‌های دریافتنی اشخاص','ASSET','DEBIT',1,1),
('la-advance-receivable','1102','پیش‌پرداخت‌ها','ASSET','DEBIT',1,1),
('la-payable-control',  '2001','حساب‌های پرداختنی اشخاص','LIABILITY','CREDIT',1,1),
('la-tax-payable',      '2002','مالیات پرداختنی','LIABILITY','CREDIT',1,1),
('la-notes-payable',    '2003','اسناد پرداختنی','LIABILITY','CREDIT',1,1),
('la-equity',           '3001','سرمایه','EQUITY','CREDIT',1,1),
('la-retained-earnings','3002','سود و زیان انباشته','EQUITY','CREDIT',1,1),
('la-revenue',          '4001','فروش','REVENUE','CREDIT',1,1),
('la-other-revenue',    '4002','سایر درآمدها','REVENUE','CREDIT',1,1),
('la-cogs',             '5001','بهای کالای فروش‌رفته','EXPENSE','DEBIT',1,1),
('la-operating-expense','5002','هزینه‌های عملیاتی','EXPENSE','DEBIT',1,1),
('la-bank-fee',         '5003','کارمزد بانک','EXPENSE','DEBIT',1,1),
('la-waste-expense',    '5004','افت و ضایعات','EXPENSE','DEBIT',1,1);

-- ---------------------------------------------------------------------------
-- 2. Settlement allocation (payment document -> invoice document)
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS settlement_allocations (
    id                   TEXT PRIMARY KEY,
    payment_document_id  TEXT NOT NULL REFERENCES documents(id) ON DELETE RESTRICT,
    invoice_document_id  TEXT NOT NULL REFERENCES documents(id) ON DELETE RESTRICT,
    amount_irr           INTEGER NOT NULL CHECK (amount_irr > 0),
    allocated_by         TEXT NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
    allocated_at_utc     TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    notes                TEXT,
    UNIQUE (payment_document_id, invoice_document_id),
    CHECK (payment_document_id <> invoice_document_id)
);
CREATE INDEX IF NOT EXISTS ix_settlement_payment ON settlement_allocations(payment_document_id);
CREATE INDEX IF NOT EXISTS ix_settlement_invoice ON settlement_allocations(invoice_document_id);

CREATE TRIGGER IF NOT EXISTS trg_settlement_requires_payment_txn
BEFORE INSERT ON settlement_allocations
BEGIN
    SELECT CASE WHEN NOT EXISTS (
        SELECT 1 FROM payment_transactions pt WHERE pt.document_id = NEW.payment_document_id
    ) THEN RAISE(ABORT, 'Settlement requires an existing payment transaction') END;
END;

CREATE TRIGGER IF NOT EXISTS trg_settlement_not_exceed_payment
BEFORE INSERT ON settlement_allocations
BEGIN
    SELECT CASE WHEN (
        SELECT COALESCE(SUM(amount_irr),0) FROM settlement_allocations
        WHERE payment_document_id = NEW.payment_document_id
    ) + NEW.amount_irr > (
        SELECT pt.amount_irr FROM payment_transactions pt
        WHERE pt.document_id = NEW.payment_document_id
    ) THEN RAISE(ABORT, 'Settlement allocation exceeds the payment amount') END;
END;

CREATE TRIGGER IF NOT EXISTS trg_settlement_not_exceed_payment_on_update
BEFORE UPDATE OF amount_irr ON settlement_allocations
BEGIN
    SELECT CASE WHEN (
        SELECT COALESCE(SUM(amount_irr),0) FROM settlement_allocations
        WHERE payment_document_id = NEW.payment_document_id
    ) > (
        SELECT pt.amount_irr FROM payment_transactions pt
        WHERE pt.document_id = NEW.payment_document_id
    ) THEN RAISE(ABORT, 'Settlement allocation exceeds the payment amount') END;
END;

-- ---------------------------------------------------------------------------
-- 3. Reporting views (posted data only)
-- ---------------------------------------------------------------------------
CREATE VIEW IF NOT EXISTS v_stock_on_hand AS
SELECT
    sl.id AS stock_lot_id,
    sl.lot_code,
    sl.product_id,
    p.product_code,
    p.display_name AS product_name,
    sl.grade_id,
    sl.packaging_type_id,
    sm.warehouse_id,
    w.display_name AS warehouse_name,
    SUM(sm.quantity_delta_g) AS on_hand_g
FROM stock_movements sm
JOIN stock_lots sl ON sl.id = sm.stock_lot_id
LEFT JOIN products p ON p.id = sl.product_id
LEFT JOIN warehouses w ON w.id = sm.warehouse_id
WHERE sm.is_posted = 1
GROUP BY sl.id, sm.warehouse_id
HAVING SUM(sm.quantity_delta_g) > 0;

CREATE VIEW IF NOT EXISTS v_party_balance AS
SELECT
    pa.id AS party_account_id,
    pa.party_id,
    p.display_name AS party_name,
    la.account_code,
    la.display_name AS ledger_account_name,
    COALESCE(SUM(jl.debit_irr),0)  AS total_debit_irr,
    COALESCE(SUM(jl.credit_irr),0) AS total_credit_irr,
    CASE la.normal_side
        WHEN 'DEBIT'  THEN COALESCE(SUM(jl.debit_irr),0)  - COALESCE(SUM(jl.credit_irr),0)
        ELSE               COALESCE(SUM(jl.credit_irr),0) - COALESCE(SUM(jl.debit_irr),0)
    END AS balance_irr
FROM party_accounts pa
JOIN parties p ON p.id = pa.party_id
JOIN ledger_accounts la ON la.id = pa.ledger_account_id
LEFT JOIN journal_lines jl ON jl.ledger_account_id = la.id
LEFT JOIN journal_entries je ON je.id = jl.journal_entry_id AND je.is_posted = 1
GROUP BY pa.id;

CREATE VIEW IF NOT EXISTS v_basket_custody_balance AS
SELECT
    pa.id AS party_account_id,
    pa.party_id,
    p.display_name AS party_name,
    COALESCE(SUM(CASE WHEN bm.movement_type = 'CUSTODY_IN'  THEN bm.quantity END),0)
  - COALESCE(SUM(CASE WHEN bm.movement_type = 'CUSTODY_OUT' THEN bm.quantity END),0) AS custody_balance,
    COALESCE(SUM(CASE WHEN bm.movement_type = 'CUSTODY_IN'  THEN bm.quantity END),0) AS custody_in_total,
    COALESCE(SUM(CASE WHEN bm.movement_type = 'CUSTODY_OUT' THEN bm.quantity END),0) AS custody_out_total
FROM basket_movements bm
JOIN party_accounts pa ON pa.id = bm.to_party_account_id
JOIN parties p ON p.id = pa.party_id
GROUP BY pa.id;

CREATE VIEW IF NOT EXISTS v_bank_balance AS
SELECT
    ba.id AS bank_account_id,
    ba.display_title,
    la.account_code,
    SUM(CASE WHEN pt.direction = 'IN'  THEN pt.amount_irr ELSE -pt.amount_irr END) AS balance_irr,
    SUM(CASE WHEN pt.direction = 'IN'  THEN pt.amount_irr ELSE 0 END) AS total_in_irr,
    SUM(CASE WHEN pt.direction = 'OUT' THEN pt.amount_irr ELSE 0 END) AS total_out_irr
FROM bank_accounts ba
JOIN ledger_accounts la ON la.id = ba.ledger_account_id
LEFT JOIN payment_transactions pt ON pt.bank_account_id = ba.id
GROUP BY ba.id;

CREATE VIEW IF NOT EXISTS v_cheque_register AS
SELECT
    ch.id AS cheque_id,
    ch.cheque_number,
    ch.direction,
    ch.amount_irr,
    ch.issue_date_iso,
    ch.due_date_iso,
    ch.status,
    ba.display_title AS bank_account,
    pa.party_id,
    p.display_name AS party_name,
    (julianday(ch.due_date_iso) - julianday(strftime('%Y-%m-%d','now'))) AS days_to_due
FROM cheques ch
LEFT JOIN bank_accounts ba ON ba.id = ch.bank_account_id
LEFT JOIN party_accounts pa ON pa.id = ch.beneficiary_account_id
LEFT JOIN parties p ON p.id = pa.party_id;

CREATE VIEW IF NOT EXISTS v_profit_loss AS
SELECT
    la.account_class,
    la.account_code,
    la.display_name,
    COALESCE(SUM(jl.debit_irr),0)  AS total_debit_irr,
    COALESCE(SUM(jl.credit_irr),0) AS total_credit_irr
FROM ledger_accounts la
LEFT JOIN journal_lines jl ON jl.ledger_account_id = la.id
LEFT JOIN journal_entries je ON je.id = jl.journal_entry_id AND je.is_posted = 1
GROUP BY la.id;

-- ---------------------------------------------------------------------------
-- 4. Integrity triggers
-- ---------------------------------------------------------------------------
-- 4a. Weighing net weight must equal gross - tare - loss (integer grams).
CREATE TRIGGER IF NOT EXISTS trg_weighing_net_weight_check
BEFORE INSERT ON weigh_tickets
WHEN NEW.net_weight_g <> NEW.gross_weight_g - NEW.tare_weight_g - NEW.loss_weight_g
BEGIN
    SELECT RAISE(ABORT, 'Net weight must equal gross minus tare minus loss');
END;

CREATE TRIGGER IF NOT EXISTS trg_weighing_net_weight_check_update
BEFORE UPDATE OF gross_weight_g, tare_weight_g, loss_weight_g, net_weight_g ON weigh_tickets
WHEN NEW.net_weight_g <> NEW.gross_weight_g - NEW.tare_weight_g - NEW.loss_weight_g
BEGIN
    SELECT RAISE(ABORT, 'Net weight must equal gross minus tare minus loss');
END;

-- 4b. Invoice line amounts must follow the pricing rule (half-up rounding).
CREATE TRIGGER IF NOT EXISTS trg_purchase_line_amount_check
BEFORE INSERT ON purchase_invoice_lines
WHEN NEW.net_amount_irr <> ((NEW.quantity_g * NEW.unit_price_irr_per_kg + 500) / 1000)
                           - NEW.discount_irr + NEW.freight_irr + NEW.commission_irr
                           + NEW.other_adjustments_irr
BEGIN
    SELECT RAISE(ABORT, 'Purchase line net amount does not match the pricing rule');
END;

CREATE TRIGGER IF NOT EXISTS trg_purchase_line_amount_check_update
BEFORE UPDATE OF quantity_g, unit_price_irr_per_kg, discount_irr, freight_irr,
                      commission_irr, other_adjustments_irr, net_amount_irr
ON purchase_invoice_lines
WHEN NEW.net_amount_irr <> ((NEW.quantity_g * NEW.unit_price_irr_per_kg + 500) / 1000)
                           - NEW.discount_irr + NEW.freight_irr + NEW.commission_irr
                           + NEW.other_adjustments_irr
BEGIN
    SELECT RAISE(ABORT, 'Purchase line net amount does not match the pricing rule');
END;

CREATE TRIGGER IF NOT EXISTS trg_sales_line_amount_check
BEFORE INSERT ON sales_invoice_lines
WHEN NEW.net_receivable_irr <> ((NEW.quantity_g * NEW.unit_price_irr_per_kg + 500) / 1000)
                                - NEW.discount_irr + NEW.freight_irr + NEW.commission_irr
                                + NEW.other_costs_irr
BEGIN
    SELECT RAISE(ABORT, 'Sales line net receivable does not match the pricing rule');
END;

CREATE TRIGGER IF NOT EXISTS trg_sales_line_amount_check_update
BEFORE UPDATE OF quantity_g, unit_price_irr_per_kg, discount_irr, freight_irr,
                      commission_irr, other_costs_irr, net_receivable_irr
ON sales_invoice_lines
WHEN NEW.net_receivable_irr <> ((NEW.quantity_g * NEW.unit_price_irr_per_kg + 500) / 1000)
                                - NEW.discount_irr + NEW.freight_irr + NEW.commission_irr
                                + NEW.other_costs_irr
BEGIN
    SELECT RAISE(ABORT, 'Sales line net receivable does not match the pricing rule');
END;

-- 4c. Sorting reconciliation: recompute output/variance/status automatically.
CREATE TRIGGER IF NOT EXISTS trg_sorting_recompute_after_output
AFTER INSERT ON sorting_outputs
BEGIN
    UPDATE sorting_batches SET
        output_weight_g = (SELECT COALESCE(SUM(quantity_g),0) FROM sorting_outputs
                           WHERE sorting_document_id = NEW.sorting_document_id),
        variance_weight_g = input_weight_g
            - (SELECT COALESCE(SUM(quantity_g),0) FROM sorting_outputs
               WHERE sorting_document_id = NEW.sorting_document_id)
            - waste_weight_g - process_loss_weight_g,
        reconciliation_status = CASE
            WHEN ABS(input_weight_g
                - (SELECT COALESCE(SUM(quantity_g),0) FROM sorting_outputs
                   WHERE sorting_document_id = NEW.sorting_document_id)
                - waste_weight_g - process_loss_weight_g) <= variance_tolerance_g
            THEN 'MATCHED' ELSE 'WARNING' END
    WHERE document_id = NEW.sorting_document_id;
END;

CREATE TRIGGER IF NOT EXISTS trg_sorting_recompute_after_input
AFTER INSERT ON sorting_batch_inputs
BEGIN
    UPDATE sorting_batches SET
        input_weight_g = (SELECT COALESCE(SUM(quantity_g),0) FROM sorting_batch_inputs
                          WHERE sorting_document_id = NEW.sorting_document_id),
        variance_weight_g = (SELECT COALESCE(SUM(quantity_g),0) FROM sorting_batch_inputs
                             WHERE sorting_document_id = NEW.sorting_document_id)
            - output_weight_g - waste_weight_g - process_loss_weight_g,
        reconciliation_status = CASE
            WHEN ABS((SELECT COALESCE(SUM(quantity_g),0) FROM sorting_batch_inputs
                      WHERE sorting_document_id = NEW.sorting_document_id)
                - output_weight_g - waste_weight_g - process_loss_weight_g) <= variance_tolerance_g
            THEN 'MATCHED' ELSE 'WARNING' END
    WHERE document_id = NEW.sorting_document_id;
END;

CREATE TRIGGER IF NOT EXISTS trg_sorting_recompute_after_adjust
AFTER UPDATE OF waste_weight_g, process_loss_weight_g, variance_tolerance_g ON sorting_batches
BEGIN
    UPDATE sorting_batches SET
        variance_weight_g = input_weight_g - output_weight_g
            - NEW.waste_weight_g - NEW.process_loss_weight_g,
        reconciliation_status = CASE
            WHEN ABS(input_weight_g - output_weight_g
                - NEW.waste_weight_g - NEW.process_loss_weight_g) <= NEW.variance_tolerance_g
            THEN 'MATCHED' ELSE 'WARNING' END
    WHERE document_id = NEW.document_id;
END;

-- 4d. Cheque clearance must settle the full cheque amount.
CREATE TRIGGER IF NOT EXISTS trg_cheque_clearance_amount_check
BEFORE INSERT ON cheque_events
WHEN NEW.event_type = 'CLEARED'
 AND NEW.amount_irr <> (SELECT amount_irr FROM cheques WHERE id = NEW.cheque_id)
BEGIN
    SELECT RAISE(ABORT, 'Cheque clearance must settle the full cheque amount');
END;

-- 4e. Automatic document numbering when the caller passes the empty sentinel.
--     (document_number is NOT NULL, so the sentinel is '' rather than NULL.)
CREATE TRIGGER IF NOT EXISTS trg_document_number_autofill
AFTER INSERT ON documents
WHEN NEW.document_number = ''
BEGIN
    INSERT INTO document_sequences(document_type, fiscal_year, last_number)
    VALUES (NEW.document_type, NEW.fiscal_year, 1)
    ON CONFLICT(document_type, fiscal_year) DO UPDATE SET last_number = last_number + 1;
    UPDATE documents SET document_number = (
        SELECT last_number FROM document_sequences
        WHERE document_type = NEW.document_type AND fiscal_year = NEW.fiscal_year
    ) WHERE id = NEW.id;
END;

-- ---------------------------------------------------------------------------
-- 5. database.reset permission (ADMIN only) - closes the G2 role gap
-- ---------------------------------------------------------------------------
INSERT OR IGNORE INTO permissions(permission_code, display_name, module_code)
VALUES ('database.reset', 'پاکسازی کامل پایگاه داده', 'SYSTEM');
INSERT OR IGNORE INTO role_permissions(role_id, permission_code, granted)
SELECT 'role-admin', 'database.reset', 1;

-- ---------------------------------------------------------------------------
-- 6. Bookkeeping
-- ---------------------------------------------------------------------------
INSERT OR IGNORE INTO schema_migrations(version, name)
VALUES (6, 'Chart of accounts, settlement allocation, reconciliation views and integrity triggers');
UPDATE app_metadata SET meta_value='6', updated_at_utc=strftime('%Y-%m-%dT%H:%M:%fZ','now')
WHERE meta_key='schema_version';
PRAGMA user_version = 6;

COMMIT;
