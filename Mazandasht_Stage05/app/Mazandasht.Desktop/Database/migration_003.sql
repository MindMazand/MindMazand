-- Migration 003: operational sequences and safe defaults for Stage 04.
BEGIN IMMEDIATE;

CREATE INDEX IF NOT EXISTS ix_products_group_name_active ON products(product_group_id, display_name, is_active);

CREATE TABLE IF NOT EXISTS document_sequences (
    document_type TEXT NOT NULL,
    fiscal_year TEXT NOT NULL,
    last_number INTEGER NOT NULL CHECK(last_number >= 0),
    PRIMARY KEY(document_type, fiscal_year)
);

INSERT OR IGNORE INTO units(id,unit_code,display_name,base_unit_code,conversion_numerator,conversion_denominator,is_active)
VALUES('unit-kg','KG','کیلوگرم','G',1000,1,1);
INSERT OR IGNORE INTO units(id,unit_code,display_name,base_unit_code,conversion_numerator,conversion_denominator,is_active)
VALUES('unit-g','G','گرم','G',1,1,1);

INSERT OR IGNORE INTO main_groups(id,group_code,display_name,notes,is_active)
VALUES('mg-weigh-in','SYS-WEIGH-IN','ورود باسکول','گروه سیستمی عملیات توزین ورودی',1);
INSERT OR IGNORE INTO main_groups(id,group_code,display_name,notes,is_active)
VALUES('mg-weigh-out','SYS-WEIGH-OUT','خروج باسکول','گروه سیستمی عملیات توزین خروجی',1);

INSERT OR IGNORE INTO operations(id,operation_code,display_name,direction,main_group_id,notes,is_active)
VALUES('op-weigh-in','SYS-WEIGH-IN','توزین ورودی','IN','mg-weigh-in','عملیات پایه توزین؛ کدهای واقعی INFO بدون تغییر حفظ می‌شوند',1);
INSERT OR IGNORE INTO operations(id,operation_code,display_name,direction,main_group_id,notes,is_active)
VALUES('op-weigh-out','SYS-WEIGH-OUT','توزین خروجی','OUT','mg-weigh-out','عملیات پایه توزین؛ کدهای واقعی INFO بدون تغییر حفظ می‌شوند',1);


CREATE TRIGGER IF NOT EXISTS trg_stock_movement_no_negative_balance_before_post
BEFORE UPDATE OF is_posted ON stock_movements
WHEN NEW.is_posted = 1 AND OLD.is_posted = 0
BEGIN
    SELECT CASE WHEN (
        SELECT COALESCE(SUM(quantity_delta_g),0)
        FROM stock_movements
        WHERE stock_lot_id=NEW.stock_lot_id AND warehouse_id=NEW.warehouse_id AND is_posted=1
    ) + NEW.quantity_delta_g < 0
    THEN RAISE(ABORT, 'Insufficient stock balance for posting this movement') END;
END;

INSERT OR IGNORE INTO schema_migrations(version,name)
VALUES(3,'Operational sequences and Stage 04 weighing defaults');
UPDATE app_metadata SET meta_value='3', updated_at_utc=strftime('%Y-%m-%dT%H:%M:%fZ','now')
WHERE meta_key='schema_version';
PRAGMA user_version=3;
COMMIT;
