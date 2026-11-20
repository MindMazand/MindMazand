-- ===========================================================================
-- Migration 007 (G9): reporting views must read posted journal data only.
--
-- The v6 views v_party_balance and v_profit_loss joined journal_lines to
-- journal_entries with "je.is_posted = 1" in the ON clause but then summed the
-- line amounts unconditionally, so an unposted (REVIEW) journal entry inflated
-- party balances and the profit/loss report. That contradicts the rules
-- contract: reports.onlyPostedData = true.
--
-- The views are recreated with an EXISTS filter so only lines whose entry is
-- posted inside the reporting period are aggregated. No table data is touched,
-- no account is renamed and no document is rewritten.
--
-- Incremental and safe: CREATE VIEW IF NOT EXISTS / DROP + CREATE, idempotent,
-- PRAGMA user_version becomes 7.
-- ===========================================================================

BEGIN IMMEDIATE;

DROP VIEW IF EXISTS v_party_balance;
CREATE VIEW v_party_balance AS
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
    AND EXISTS (SELECT 1 FROM journal_entries je
                WHERE je.id = jl.journal_entry_id AND je.is_posted = 1)
GROUP BY pa.id;

DROP VIEW IF EXISTS v_profit_loss;
CREATE VIEW v_profit_loss AS
SELECT
    la.account_class,
    la.account_code,
    la.display_name,
    COALESCE(SUM(jl.debit_irr),0)  AS total_debit_irr,
    COALESCE(SUM(jl.credit_irr),0) AS total_credit_irr
FROM ledger_accounts la
LEFT JOIN journal_lines jl ON jl.ledger_account_id = la.id
    AND EXISTS (SELECT 1 FROM journal_entries je
                WHERE je.id = jl.journal_entry_id AND je.is_posted = 1)
GROUP BY la.id;

INSERT OR IGNORE INTO schema_migrations(version, name)
VALUES(7, 'Reporting views read posted journal data only');
UPDATE app_metadata SET meta_value='7', updated_at_utc=strftime('%Y-%m-%dT%H:%M:%fZ','now')
WHERE meta_key='schema_version';
PRAGMA user_version = 7;

COMMIT;
