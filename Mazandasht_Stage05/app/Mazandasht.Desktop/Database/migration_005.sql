-- Migration 005: reusable business operation catalogue and sample bank directory.
BEGIN IMMEDIATE;

CREATE TABLE IF NOT EXISTS bank_catalog (
    id              TEXT PRIMARY KEY,
    bank_code       TEXT NOT NULL UNIQUE,
    display_name    TEXT NOT NULL UNIQUE,
    is_system       INTEGER NOT NULL DEFAULT 1 CHECK (is_system IN (0,1)),
    is_active       INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0,1)),
    created_at_utc  TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);

ALTER TABLE bank_accounts ADD COLUMN bank_catalog_id TEXT
    REFERENCES bank_catalog(id) ON DELETE RESTRICT;

CREATE INDEX IF NOT EXISTS ix_bank_catalog_active_name
    ON bank_catalog(is_active, display_name);

-- Sample institutions only. These rows are not bank accounts and contain no
-- account number, IBAN, owner, balance, or transaction.
INSERT OR IGNORE INTO bank_catalog(id,bank_code,display_name,is_system,is_active) VALUES
('bank-saderat','SADERAT','بانک صادرات',1,1),
('bank-melli','MELLI','بانک ملی ایران',1,1),
('bank-mellat','MELLAT','بانک ملت',1,1),
('bank-sepah','SEPAH','بانک سپه',1,1),
('bank-keshavarzi','KESHAVARZI','بانک کشاورزی',1,1),
('bank-tejarat','TEJARAT','بانک تجارت',1,1),
('bank-refah','REFAH','بانک رفاه کارگران',1,1),
('bank-pasargad','PASARGAD','بانک پاسارگاد',1,1),
('bank-saman','SAMAN','بانک سامان',1,1),
('bank-shahr','SHAHR','بانک شهر',1,1),
('bank-mehr','MEHR','بانک قرض‌الحسنه مهر ایران',1,1),
('bank-post','POST','پست بانک ایران',1,1);

-- Extensible operation taxonomy: visible defaults, not business documents.
INSERT OR IGNORE INTO main_groups(id,group_code,display_name,notes,is_active) VALUES
('mg-trade','SYS-TRADE','بازرگانی','گروه پایه عملیات خرید و فروش',1),
('mg-sorting','SYS-SORTING','سورتینگ و بسته‌بندی','گروه پایه فرآوری و درجه‌بندی محصول',1),
('mg-stock','SYS-STOCK','انبار و لجستیک','گروه پایه ورود، خروج و انتقال موجودی',1),
('mg-basket','SYS-BASKET','سبد و امانی','گروه پایه گردش ظروف و سبد',1),
('mg-finance','SYS-FINANCE','مالی و تسویه','گروه پایه دریافت، پرداخت، بانک و چک',1),
('mg-partner','SYS-PARTNER','شراکت','گروه پایه عملیات شراکتی',1);

INSERT OR IGNORE INTO operations(id,operation_code,display_name,direction,main_group_id,notes,is_active) VALUES
('op-inbound-receipt','SYS-INBOUND-RECEIPT','ورود بار','IN','mg-stock','ثبت دریافت فیزیکی محموله',1),
('op-purchase','SYS-PURCHASE','خرید کالا','NEUTRAL','mg-trade','ثبت تعهد خرید و فاکتور خرید',1),
('op-sale','SYS-SALE','فروش کالا','NEUTRAL','mg-trade','ثبت تعهد فروش و فاکتور فروش',1),
('op-sorting','SYS-SORTING','سورتینگ و درجه‌بندی','NEUTRAL','mg-sorting','تبدیل محموله ورودی به خروجی‌های درجه‌بندی‌شده',1),
('op-packaging','SYS-PACKAGING','بسته‌بندی','NEUTRAL','mg-sorting','ثبت عملیات بسته‌بندی محصول',1),
('op-stock-transfer','SYS-STOCK-TRANSFER','انتقال بین انبارها','NEUTRAL','mg-stock','انتقال موجودی بدون ایجاد خرید یا فروش',1),
('op-basket-purchase','SYS-BASKET-PURCHASE','خرید سبد','NEUTRAL','mg-basket','خرید سبد و ایجاد گردش فیزیکی و مالی',1),
('op-basket-custody-out','SYS-BASKET-CUSTODY-OUT','تحویل امانی سبد','OUT','mg-basket','خروج سبد از انبار به حساب امانی شخص',1),
('op-basket-custody-in','SYS-BASKET-CUSTODY-IN','برگشت سبد امانی','IN','mg-basket','بازگشت سبد از شخص به انبار',1),
('op-cash-receipt','SYS-CASH-RECEIPT','دریافت وجه','IN','mg-finance','ثبت دریافت وجه و تسویه حساب',1),
('op-cash-payment','SYS-CASH-PAYMENT','پرداخت وجه','OUT','mg-finance','ثبت پرداخت وجه و تسویه حساب',1),
('op-cheque-receive','SYS-CHEQUE-RECEIVE','دریافت چک','IN','mg-finance','ثبت چک دریافتی',1),
('op-cheque-issue','SYS-CHEQUE-ISSUE','صدور چک','OUT','mg-finance','ثبت چک پرداختی',1),
('op-settlement','SYS-SETTLEMENT','تخصیص تسویه','NEUTRAL','mg-finance','تخصیص پرداخت به فاکتور بدون سند مالی تکراری',1),
('op-partner-inbound','SYS-PARTNER-INBOUND','ورود بار شراکتی','IN','mg-partner','ثبت بار شریک و محاسبه سهم‌ها',1),
('op-stock-valuation','SYS-STOCK-VALUATION','ارزش‌گذاری موجودی','NEUTRAL','mg-stock','ثبت مبنای بهای تمام‌شده موجودی',1);

INSERT OR IGNORE INTO schema_migrations(version,name)
VALUES(5,'Reusable operation catalogue and sample bank directory');
UPDATE app_metadata SET meta_value='5', updated_at_utc=strftime('%Y-%m-%dT%H:%M:%fZ','now')
WHERE meta_key='schema_version';
PRAGMA user_version=5;
COMMIT;
