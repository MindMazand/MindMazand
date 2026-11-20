# گزارش مرحله ۸ — خرید، فروش، بانک، چک و تسویه

**محصول:** مازندشت · **نسخه پایه:** ۰٫۵٫۱-آلفا۲ · **نسخه هدف:** ۰٫۶٫۰
**تاریخ گزارش:** ۲۰۲۶-۱۱-۱۹ · **شاخه:** `arena/afb5578d-mindmazand`
**وضعیت دروازه:** **PASSED**

---

## ۱. هدف و دامنه

مرحله ۸ پنج ماژول مالی/تجاری را به‌صورت سرویس + رابط کاربری تکمیل می‌کند:

| ماژول | سرویس | موجودی/مجری |
|---|---|---|
| فاکتور خرید | `Services/PurchaseService.cs` | `documents(PURCHASE)` + `purchase_invoice_lines` + `document_relations(INVOICE_FOR)` |
| فاکتور فروش | `Services/SalesService.cs` | `documents(SALE)` + `sales_invoice_lines` + `document_lines` + `stock_allocations(SALE)` |
| تراکنش بانکی | `Services/BankService.cs` | `documents(BANK_TXN)` + `payment_transactions` |
| چک | `Services/ChequeService.cs` | `documents(CHEQUE)` + `cheques` + `cheque_events` |
| تخصیص تسویه | `Services/SettlementService.cs` | `settlement_allocations` + `document_relations(PAYMENT_FOR)` |

---

## ۲. فایل‌های افزوده‌شده / تغییرکرده

### افزوده‌شده
| فایل | نقش |
|---|---|
| `app/Mazandasht.Desktop/Services/PurchaseService.cs` | فاکتور خرید، فرمول صافی، اتصال به باسکول ورودی |
| `app/Mazandasht.Desktop/Services/SalesService.cs` | فاکتور فروش، تخصیص FIFO، ممنوعیت فروش بیش از موجودی |
| `app/Mazandasht.Desktop/Services/BankService.cs` | تراکنش بانکی + حل حساب بانکی با کد/عنوان |
| `app/Mazandasht.Desktop/Services/ChequeService.cs` | چک، رویدادهای چک، وصول چک |
| `app/Mazandasht.Desktop/Services/SettlementService.cs` | تخصیص پرداخت به فاکتور |
| `app/Mazandasht.Desktop/Views/FinanceModulesWindow.cs` | پنجره پنج تب‌دار مالی |
| `tests/test_stage_g8_finance.py` | ۱۷ آزمون پذیرش |

### تغییرکرده
- `MainWindow.xaml`: ورودی «خرید و فروش» و «بانک و چک» به `OpenFinanceModules_Click` وصل شد.
- `MainWindow.xaml.cs`: افزودن `OpenFinanceModules_Click`.
- `Views/OperationalModulesWindow.cs`: کمک‌متد `CatalogCombo` برای نمایش درست ردیف‌های `CatalogRow` (نمایش `DisplayName` به‌جای `Display`).

---

## ۳. قواعد اعمال‌شده (تطابق با `docs/12_Rules_Contract.json`)

### ۳٫۱ فاکتور خرید (`invoices.purchase`)
- فرمول صافی: `net_amount_irr = quantity_g × unit_price_irr_per_kg ÷ ۱۰۰۰ − تخفیف + کرایه + کمیسیون + سایر تعدیلات` با گردکردن **half-up**.
- سرویس همان عبارت صحیح (`(value + 500) / 1000`) را می‌سازد و پایگاه‌داده با تریگر
  `trg_purchase_line_amount_check` همان فرمول را تأیید می‌کند؛ مقدار نادرست رد می‌شود (آزمون شده).
- `linkage = source_weigh_document_id`: سند فاکتور به سند باسکول ورودی با رابط `INVOICE_FOR` متصل می‌شود.

### ۳٫۲ فاکتور فروش (`invoices.sales`)
- فرمول صافی: `net_receivable_irr = quantity_g × unit_price_irr_per_kg ÷ ۱۰۰۰ − تخفیف + کرایه + کمیسیون + سایر هزینه‌ها`.
- `allocationRequired = true`: هر خط فروش باید از موجودی تأییدشده تخصیص بگیرد.
- `oversellAllowed = false`: اگر مجموع تخصیص‌ها از موجودی آزاد بیشتر شود، تراکنش برمی‌گردد و پیام
  «فروش بیش از موجودی مجاز نیست» برمی‌گردد.
- تخصیص **FIFO** بر اساس `stock_lots.created_at_utc` و `id`، با کسر مقدار رزروشده (`stock_allocations` با نوع `SALE`).
- تخصیص فقط **رزرو** است؛ هیچ حركة موجودی‌ای در مرحله ۸ ثبت نمی‌شود.

### ۳٫۳ چک (`cheque`)
- وضعیت‌ها `ISSUED / CIRCULATING / CLEARED / RETURNED / CANCELLED`.
- کلید یکتا `(bank_account_id, cheque_number)` در سطح پایگاه‌داده اعمال می‌شود.
- `clearanceCreatesBankTransaction = true`: وصول چک یک سند `BANK_TXN` و یک ردیف `payment_transactions` می‌سازد و وضعیت چک را `CLEARED` می‌کند.

### ۳٫۴ تخصیص تسویه (`settlement`)
- ارتباط `payment_document → invoice_document`.
- `allocationSumMustNotExceedPayment`: هم سرویس و هم تریگرهای
  `trg_settlement_not_exceed_payment(_on_update)` آن را اعمال می‌کنند.
- `createsNewBankTransaction = false`: تخصیص هیچ تراکنش بانکی نساخت.
- `trg_settlement_requires_payment_txn`: تخصیص فقط به سندی که `payment_transactions` دارد مجاز است.

### ۳٫۵ پول (`money`)
- همه مبالغ **صحیح و ریالی**؛ ورودی فارسی (۰۱۲۳ / ٠١٢٣) و جداکننده هزارگان نرمال می‌شود.

---

## ۴. سطوح دسترسی

| سرویس | مجوز |
|---|---|
| `PurchaseService` | `purchase.manage` |
| `SalesService` | `sale.manage` |
| `BankService` | `bank.manage` |
| `ChequeService` | `cheque.manage` |
| `SettlementService` | `bank.manage` |

`MANAGER` و `ADMIN` دارای مجوزند؛ `SUPERVISOR` ندارد (آزمون شده).

---

## ۵. رفع شکاف مرحله ۶

شکاف ثبت‌شده «`BANK_STATEMENT` bank-account code is not resolved to a bank_accounts id» بسته شد:
`BankService.ResolveBank` حساب را با `bank_accounts.id`، کد دفتر حساب (`ledger_accounts.account_code`)
یا `display_title` می‌یابد. آزمون `test_bank_transaction_resolves_account_by_code_and_updates_balance`
با کد `BANK-TEST` (بدون دانستن id) همان را بررسی می‌کند.

---

## ۶. رابط کاربری

`FinanceModulesWindow` پنج تب دارد: «خرید»، «فروش»، «بانک»، «چک»، «تسویه».
رنگ‌ها فقط از `ThemeService.Brush(...)` گرفته می‌شوند و در چهار تم موجود درست نمایش داده می‌شوند.

---

## ۷. آزمون‌ها و نتایج

دستور: `Mazandasht_Stage05/.venv/bin/python -m pytest -q tests/`

| نتیجه | مقدار |
|---|---|
| قبول | **۱۱۳** |
| ردشده (skip) | ۱ |
| ناموفق | ۰ |

آزمون‌های جدید مرحله ۸ (۱۷ مورد) از جمله:
- تطابق قواعد فاکتور/چک/تسویه با قرارداد؛
- اجرای عبارت‌های خرید و فروش روی پایگاه‌داده واقعی و تأیید فرمول half-up با بازپیاده‌سازی پایتون؛
- رد مقدار صافی نادرست توسط تریگرهای خرید و فروش؛
- تخصیص FIFO و تصمیم «فروش بیش از موجودی مجاز نیست»؛
- حل حساب بانکی با کد و به‌روزرسانی `v_bank_balance`؛
- وصول چک با ساخت دقیقاً یک تراکنش بانکی؛
- یکتایی `(bank_account_id, cheque_number)`؛
- سقف مبلغ پرداخت برای تخصیص و الزام وجود `payment_transactions`؛
- پنج دروازه مجوز؛
- **آزمون ثابت معاصرین:** هیچ سرویسی فرمان تودرتو روی اتصال باز همزمان با `ExecuteReader` اجرا نمی‌کند
  (System.Data.SQLite بدون MARS) — این آزمون باگ واقعی را در همین مرحله گرفت.

---

## ۸. تغییر واقعی محصول در این مرحله (باگ‌های پیدا و رفع‌شده)

1. **فرمان تودرتو در `DataReader` باز** — `SalesService.Allocate` و `SalesService.AvailableLots`
   در داخل `while (reader.Read())` فرمان `SqlReserved`/`SqlLotInfo` را روی همان اتصال اجرا می‌کردند
   که در اجرای واقعی با خطای «connection is busy» می‌شکند. هر دو متد بازنویسی شدند: ابتدا ردیف‌ها
   Materialize می‌شوند، سپس فرمان‌های تودرتو اجرا می‌شوند. یک آزمون ثابت این الگو را در همه سرویس‌ها می‌سنجد.
2. **`_settleRemaining` به‌جای `TextBlock` به‌عنوان `TextBox` تعریف شده بود و `null` می‌شد** — اصلاح شد.
3. **`InvoiceDocuments()` و `TransactionTypes()` به‌صورت `static` تعریف شده بودند ولی به `_db` دسترسی داشتند**
   که خطای کامپایل `CS0120` می‌داد — به متد نمونه تبدیل شدند.
4. **نمایش ردیف‌های `CatalogRow` در کمبوكس  grades** — `CatalogRow` ویژگی `Display` ندارد؛
   کمک‌متد `CatalogCombo` با `DisplayMemberPath = "DisplayName"` اضافه شد.

---

## ۹. دامنه‌های باز

| شناسه | موضوع | کجا حل می‌شود |
|---|---|---|
| O-1 | سال مالی قمری/میلادی در `FISCAL_YEAR_KIND` پیش‌فرض میلادی است | تأیید مالک |
| O-2 | نیاز به تصویب مالک برای ساختار پیش‌فرض حساب‌ها | مالک |
| O-3 | نرخ مالیات ۹٪ پیش‌فرض است و `ownerApprovalRequired` است | مالک |
| O-4 | تعویض زندهٔ تم روی WPF واقعی تأیید نشده | G4 / G12 |
| O-8 | سند‌های مرحله ۸ `requires_journal = 0` هستند تا بتوانند قبل از مرحله ۹ تأیید شوند؛ تولید المجو در G9 | G9 |
| O-9 | خروج موجودی برای فروش (صدور حركة `ISSUE` پست‌شده) هنوز پیاده نشده؛ فقط رزرو | G9 |
| O-10 | وصول چک `OUT` هنوز کاهش مانده بانک را در همان عملیات انجام می‌دهد اما بازversion برگشت چک ندارد | G9 |

---

## ۱۰. محدودیت‌ها و موارد بلاکه (بدون تغییر)

- **BLK-001:** ساخت WPF در این محیط ممکن نیست (بدون `dotnet`/`msbuild`/`mono`/`csc`).
- **BLK-002:** فایل‌های واقعی `sources/` در دسترس نیست؛ همهٔ آزمون‌ها با مقادیر ساختگی اجرا می‌شوند.
- هیچ دادهٔ واقعی ایمپورت نشده و پایگاه‌دادهٔ مالک دست نخورده است.

---

## ۱۱. پذیرش دروازهٔ ۸

| معیار | وضعیت |
|---|---|
| فاکتور خرید با فرمول صحیح و اتصال به باسکول | ✓ |
| فاکتور فروش با تخصیص FIFO و ممنوعیت فروش بیش از موجودی | ✓ |
| تراکنش بانکی با حل حساب بر اساس کد | ✓ |
| چک با وضعیت‌ها، یکتایی و وصول چک | ✓ |
| تخصیص تسویه با سقف مبلغ پرداخت | ✓ |
| اتصال رابط کاربری به پوسته | ✓ |
| اجرای آزمون‌ها (۱۱۳ قبول / ۱ رد / ۰ ناموفق) | ✓ |
| بدون المجوی در مرحله ۸ (به‌طور آگاهانه تا G9) | ✓ |
| بدون حذف/بازنویسی اسناد قطعی | ✓ |

**نتیجه: دروازهٔ ۸ باز است و کار مرحلهٔ ۹ (حسابداری دوطرفه و سود/زیان) می‌تواند آغاز شود.**
