-- Safe system defaults only. No real users, parties, balances, products or transactions.

INSERT OR IGNORE INTO roles(id, role_code, display_name, is_system_role, is_active) VALUES
('role-admin', 'ADMIN', 'مدیر سیستم', 1, 1),
('role-manager', 'MANAGER', 'مدیر مجموعه', 1, 1),
('role-accountant', 'ACCOUNTANT', 'حسابدار', 1, 1),
('role-scale', 'SCALE', 'مسئول باسکول', 1, 1),
('role-sales', 'SALES', 'کارشناس فروش', 1, 1),
('role-supervisor', 'SUPERVISOR', 'ناظر', 1, 1);

INSERT OR IGNORE INTO permissions(permission_code, display_name, module_code) VALUES
('dashboard.view', 'مشاهده داشبورد', 'DASHBOARD'),
('documents.create', 'ثبت سند', 'DOCUMENTS'),
('documents.edit_review', 'ویرایش سند در وضعیت بررسی', 'DOCUMENTS'),
('documents.approve', 'تأیید و قطعی‌سازی اسناد', 'DOCUMENTS'),
('documents.void', 'ابطال با سند برگشت', 'DOCUMENTS'),
('weighing.manage', 'مدیریت باسکول', 'OPERATIONS'),
('sorting.manage', 'مدیریت سورتینگ', 'OPERATIONS'),
('stock.manage', 'مدیریت انبار', 'INVENTORY'),
('purchase.manage', 'مدیریت خرید', 'TRADE'),
('sale.manage', 'مدیریت فروش', 'TRADE'),
('bank.manage', 'مدیریت بانک و تراکنش', 'FINANCE'),
('cheque.manage', 'مدیریت چک', 'FINANCE'),
('basket.manage', 'مدیریت سبد و امانی', 'OPERATIONS'),
('wage.manage', 'مدیریت دستمزد', 'HR'),
('reports.view', 'مشاهده گزارش‌ها', 'REPORTS'),
('journal.view', 'مشاهده دفاتر حسابداری', 'ACCOUNTING'),
('base.manage', 'مدیریت اطلاعات پایه', 'SETTINGS'),
('users.manage', 'مدیریت کاربران و کلیدها', 'SECURITY'),
('backup.manage', 'مدیریت پشتیبان', 'SYSTEM'),
('audit.view', 'مشاهده تاریخچه تغییرات', 'SECURITY'),
('system.settings', 'تنظیمات سیستم', 'SYSTEM');

-- Admin: all defined permissions.
INSERT OR IGNORE INTO role_permissions(role_id, permission_code, granted)
SELECT 'role-admin', permission_code, 1 FROM permissions;

-- Manager: operational oversight and approval; system identity/security settings remain admin-only.
INSERT OR IGNORE INTO role_permissions(role_id, permission_code, granted) VALUES
('role-manager','dashboard.view',1),
('role-manager','documents.create',1),
('role-manager','documents.edit_review',1),
('role-manager','documents.approve',1),
('role-manager','documents.void',1),
('role-manager','weighing.manage',1),
('role-manager','sorting.manage',1),
('role-manager','stock.manage',1),
('role-manager','purchase.manage',1),
('role-manager','sale.manage',1),
('role-manager','bank.manage',1),
('role-manager','cheque.manage',1),
('role-manager','basket.manage',1),
('role-manager','wage.manage',1),
('role-manager','reports.view',1),
('role-manager','journal.view',1),
('role-manager','base.manage',1),
('role-manager','backup.manage',1),
('role-manager','audit.view',1);

INSERT OR IGNORE INTO role_permissions(role_id, permission_code, granted) VALUES
('role-accountant','dashboard.view',1),('role-accountant','documents.create',1),('role-accountant','documents.edit_review',1),
('role-accountant','bank.manage',1),('role-accountant','cheque.manage',1),('role-accountant','wage.manage',1),
('role-accountant','reports.view',1),('role-accountant','journal.view',1),('role-accountant','backup.manage',1),
('role-scale','dashboard.view',1),('role-scale','documents.create',1),('role-scale','documents.edit_review',1),
('role-scale','weighing.manage',1),('role-scale','sorting.manage',1),('role-scale','stock.manage',1),('role-scale','basket.manage',1),
('role-sales','dashboard.view',1),('role-sales','documents.create',1),('role-sales','documents.edit_review',1),
('role-sales','sale.manage',1),('role-sales','reports.view',1),
('role-supervisor','dashboard.view',1),('role-supervisor','reports.view',1),('role-supervisor','journal.view',1),('role-supervisor','audit.view',1);
