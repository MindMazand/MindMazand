# گزارش فاز ۶ — تکمیل ورود و خروج عملیاتی

| بند | مقدار |
| --- | --- |
| فاز | G6 |
| عنوان | تکمیل Import و Export عملیاتی |
| وضعیت | **PASSED** |
| گیت فاز | **PASSED** |
| نسخهٔ اسکیمای پایگاه داده | ۶ (بدون تغییر) |
| تست‌ها | ۸۵ موفق، ۱ ردشده، ۰ ناموفق |
| دستور تست | `.venv/bin/python -m pytest -q tests/` |
| محیط ساخت WPF | در دسترس نیست (BLK-001) |

---

## ۱. وضعیت ورودی فاز

در نسخهٔ پایه، مسیر «ورود اطلاعات Excel / CSV» تا این مرحله کامل بود: فایل خوانده
می‌شد، پیش‌نمایش داده می‌شد، ردیف‌ها رمزگذاری‌شده و رهاشده (staging) می‌شدند، و
امضای SHA-256 فایل ذخیره می‌شد. اما **هیچ مسیری برای انتقال واقعی آن ردیف‌ها به
جداول عملیاتی وجود نداشت** و هیچ خروجی عملیاتی هم نداشتیم. این همان شکاف بود.

قاعدهٔ مالک در این فاز نگه داشته شد: **وارد کردن دادهٔ واقعی فقط با پیش‌نمایش**،
و هیچ رکوردی بدون پیش‌نمایش و اعتبارسنجی ساخته نمی‌شود.

---

## ۲. چیزهایی که ساخته شد

### ۲.۱ قرارداد مقصد پروفایل‌ها — `Import/ImportTargetCatalog.cs`

برای هر یک از ۱۱ پروفایل تعریف شد که ردیفِ منتقل‌شده به چه چیزی تبدیل می‌شود:

| دسته | پروفایل‌ها | نتیجه |
| --- | --- | --- |
| اطلاعات پایه | `MASTER_PARTIES`، `MASTER_PRODUCTS` | ساخت شخص/محصول با کد مرجع مالک |
| سند پیش‌نویس | `WEIGHING_IN`، `PURCHASE_INVOICES`، `SALES_INVOICES`، `SORTING`، `BASKET_MOVEMENTS`، `BANK_STATEMENT`، `CHEQUES`، `SETTLEMENTS`، `PARTNER_OPERATIONS` | سند `DRAFT` با خطوط و ارتباط به سند مرجع |

هر پروفایل فیلدهای الزامی، فیلد کلید، فیلد تاریخ، فیلد طرف حساب، فیلد محصول،
فیلد انبار و فیلد سند مرجع را اعلام می‌کند. این جدول با
`docs/11_Import_Profiles_Canonical.json` و با فرهنگ دادهٔ خودِ قالب‌های Excel
هم‌خوان است و تست آن را هم‌خوانی می‌دهد.

### ۲.۲ موتور انتقال — `Import/ImportCommitService.cs`

دو حالت دارد: `Validate` (اعتبارسنجی بدون هیچ ثبتی) و `Commit` (انتقال واقعی).
گام‌ها:

۱. بررسی مجوز `documents.create` روی کاربر واردشده.
۲. بررسی وضعیت بسته: فقط بسته‌های `PREVIEW` قابل انتقال‌اند.
۳. خواندن نگاشت ستون‌ها از `column_mapping_json`.
۴. رمزگشایی ردیف‌ها با DPAPI (همان کلیدِ مرحلهٔ staging).
۵. پر کردن مقادیر بر اساس نام فیلدهای فارسی فرهنگ داده.
۶. اعتبارسنجی فیلدهای الزامی و تبدیل تاریخ شمسی به ISO.
۷. تشخیص تکراری بودن از طریق `source_row_sha256` (اگر همان ردیف قبلاً
   `IMPORTED` شده باشد، `DUPLICATE` علامت می‌خورد).
۸. حل مراجع: کد طرف حساب، کد محصول، کد بسته‌بندی، کد انبار و شماره سند مرجع
   به شناسه‌های داخلی. اگر مرجع وجود نداشته باشد، ردیف `REJECTED` می‌شود و
   علت در `validation_errors_json` ثبت می‌شود.
۹. ثبت سند با `status='DRAFT'` و `requires_journal=0` و `requires_stock_posting=0`
   و شماره خودکار (تریگر `trg_document_number_autofill`).
۱۰. ثبت خط سند، تاریخچهٔ وضعیت و ردیف ممیزی.
۱۱. به‌روزرسانی شمارنده‌های بسته به‌صورت `accepted/rejected` و تعیین وضعیت
    `COMMITTED`.

**هیچ المجو یا موجودی‌ای در این مرحله ثبت نمی‌شود.** سند ورودی فقط یک پیش‌نویس
است و اثر مالی/انباری پس از تأیید مدیر و با موتور ماژول (فازهای ۷ تا ۹) اعمال
می‌شود. این دقیقاً همان قاعدهٔ «REVIEW بدون اثر مالی/انباری» است.

### ۲.۳ فهرست‌های پایه با کد مرجع — `MasterDataService`

دو متد جدید: `CreatePartyWithCode` و `CreateProductWithCode` که کد مرجع مالک را
دست‌نخورده می‌پذیرند، تکراری بودن را رد می‌کنند (به‌جای شماره‌گذاری خودکار) و
همراه با حساب کنترلی/گروه/واحد در یک تراکنش می‌نویسند. نسخهٔ داخلی آن‌ها
(internal) به موتور انتقال اجازه می‌دهد در تراکنش خودش بنویسد تا اتمی بماند.

### ۲.۴ سال مالی — `Services/FiscalYearService.cs`

قاعدهٔ سال مالی از پارامتر عملیاتی `FISCAL_YEAR_KIND` (`JALALI` یا `GREGORIAN`)
خوانده می‌شود. تا وقتی مالک تصمیم نگرفته، **گریگوری** بردفاع است (همان رفتار
قبلی) و یافتهٔ باز O-1 در این مورد باز می‌ماند. تبدیل تاریخ شمسی با
`System.Globalization.PersianCalendar` انجام می‌شود و متد `ResolveValue` بدون
نیاز به پایگاه داده هم تست می‌شود.

### ۲.۵ صف ورود و خروجی — `Import/ImportQueueService.cs` و `Views/ImportQueueWindow.xaml(.cs)`

پنجرهٔ «صف ورود و خروجی اطلاعات»:

- فهرست بسته‌های موجود با فایل منبع، پروفایل، مقصد، وضعیت و شمارنده‌ها؛
- مشاهدهٔ وضعیت هر ردیف (پیش‌نویس/ردشده/تکراری) و علت رد؛
- شمارش فیلدهای نگاشت‌شده و فیلدهای الزامیِ بدون نگاشت؛
- دکمه‌های «اعتبارسنجی (بدون ثبت)»، «انتقال به جداول عملیاتی»، «لغو بسته» و
  «خروجی CSV اسناد»؛
- دکمه‌های عملیاتی فقط برای بسته‌های `PREVIEW` فعال می‌شوند.

از منوی کناری گزینهٔ «صف ورود و خروجی اطلاعات» به این پنجره متصل است.

### ۲.۶ خروجی — `Import/ExportService.cs`

خروجی **CSV** (UTF-8 با BOM) از اسناد، با فارسی‌نگاری درست در Excel و ثبت هر
اجرا در `export_runs` (`STARTED` → `SUCCESS`/`FAILED` با تعداد ردیف و متن خطا).
مجوز آن `reports.view` است.

چرا CSV و نه xlsx؟ چون وابستگی‌های برنامه فقط `ExcelDataReader` (خواندن) است و
هیچ کتابخانهٔ نوشتن workbook در برنامه نیست. ادعای خروجی xlsx کذب می‌بود؛ به‌جای
آن فقط CSV واقعاً پیاده شد.

---

## ۳. تغییرات در فایل‌ها

| فایل | تغییر |
| --- | --- |
| `Import/ImportTargetCatalog.cs` | جدید — قرارداد مقصد ۱۱ پروفایل |
| `Import/ImportCommitService.cs` | جدید — اعتبارسنجی/انتقال |
| `Import/ImportQueueService.cs` | جدید — فهرست و جزئیات بسته‌ها |
| `Import/ExportService.cs` | جدید — خروجی CSV + export_runs |
| `Services/FiscalYearService.cs` | جدید — قاعده سال مالی |
| `Services/MasterDataService.cs` | `CreatePartyWithCode`، `CreateProductWithCode` |
| `Views/ImportQueueWindow.xaml(.cs)` | جدید — پنجرهٔ صف |
| `MainWindow.xaml(.cs)` | گزینهٔ ناوبری و رویهٔ `OpenImportQueue_Click` |
| `tests/test_stage_g6_import_export.py` | جدید — ۱۴ تست |

---

## ۴. نتایج تست

```
.venv/bin/python -m pytest -q tests/
85 passed, 1 skipped in 7.91s
```

| تست | بررسی |
| --- | --- |
| `test_commit_contract_matches_the_canonical_import_profiles` | قرارداد C# با فرهنگ دادهٔ ۱۱ قالب هم‌خوان |
| `test_required_fields_helper_enforces_every_bound_column` | `RequiredFields` همهٔ فیلدهای وابسته را الزامی می‌کند |
| `test_document_types_are_valid_for_the_schema_check` | نوع سندها با CHECK اسکیما |
| `test_commit_document_insert_creates_a_draft_without_posting_flags` | سند DRAFT، requires_journal=0، requires_stock_posting=0، شماره خودکار |
| `test_commit_row_and_batch_updates_keep_counters_consistent` | شمارنده‌های بسته با ردیف‌ها سازگار |
| `test_duplicate_detection_uses_the_source_row_hash` | تشخیص تکراری با اثر انگشت ردیف |
| `test_commit_permission_gate_uses_the_seeded_roles` | MANAGER مجاز، SUPERVISOR مجاز نیست |
| `test_commit_engine_never_posts_journal_or_stock` | موتور انتقال المجو/موجودی نمی‌نویسد |
| `test_export_csv_permission_gate_and_run_lifecycle_sql` | مجوز خروجی + چرخهٔ export_runs |
| `test_export_service_states_csv_only_and_records_runs` | فقط CSV؛ بدون API نوشتن workbook |
| `test_queue_queries_run_against_the_schema` | پرس‌وجوهای صف و لغو بسته |
| `test_fiscal_year_service_exposes_a_database_free_resolution` | تفکیک قاعده سال مالی |
| `test_catalog_import_sql_is_valid_against_the_schema` | SQL ساخت شخص با کد مرجع |
| `test_import_queue_window_is_wired_into_the_shell` | اتصال پنجره به منو |

---

## ۵. محدودیت‌ها و موارد باز

| کد | مورد | توضیح |
| --- | --- | --- |
| BLK-001 | ساخت WPF | پنجره‌ها در محیط لینوکس اجرا نشده‌اند |
| — | رمزگشایی DPAPI | فقط روی ویندوز و با کاربرِ رمزگذاری‌کننده ممکن است؛ در این محیط تست نشد |
| — | اثر مالی/انباری | اسناد ورودی پیش‌نویس‌اند؛ موتورهای ماژول در فازهای ۷–۹ |
| — | خروجی xlsx | عمداً پیاده نشد (بخش ۲.۶) |
| — | کد حساب بانکی در `BANK_STATEMENT` | فیلد الزامی است ولی هنوز به شناسهٔ `bank_accounts` حل نمی‌شود؛ فاز ۸ |
| O-1 | قاعده سال مالی | همچنان تصمیم مالک |

---

## ۶. یافته‌های حاصل از تست

۱. **`RequiredFields` ناقص بود.** در نسخهٔ اول، فیلدهای `DateField`، `PartyField`،
   `ProductField`، `WarehouseField` و `SourceDocumentField` در لیست الزامی‌ها
   نبودند؛ یعنی سندی بدون تاریخ یا بدون محصول می‌توانست ساخته شود. اصلاح شد و
   تست `test_required_fields_helper_enforces_every_bound_column` الان همه‌شان
   را الزامی می‌کند.
۲. **تداخل آیتم‌های Page در csproj.** افزودن صریح `Assets\Themes\*.xaml` به
   عنوان `Page` در پروژهٔ SDK-style باعث خطای آیتم تکراری می‌شود؛ حذف شد چون
   SDK خودش `**/*.xaml` را به‌عنوان Page می‌گیرد. (این مورد در فاز ۴ اصلاح شد.)

---

## ۷. گیت فاز

| معیار پذیرش | وضعیت |
| --- | --- |
| قرارداد مقصد برای ۱۱ پروفایل | ✅ |
| اعتبارسنجی بدون ثبت + انتقال واقعی | ✅ |
| بدون پیش‌نمایش، هیچ ردیفی وارد نمی‌شود | ✅ |
| بدون اثر مالی/انباری در انتقال | ✅ DRAFT + پرچم‌های صفر |
| تشخیص تکراری با اثر انگشت ردیف | ✅ |
| کد مرجع مالک دست‌نخورده | ✅ |
| رابط کاربری صف + اعتبارسنجی + لغو | ✅ |
| خروجی عملیاتی با ثبت در export_runs | ✅ CSV |
| تست‌ها | ✅ ۸۵ موفق |
| ساخت WPF روی ویندوز | ⏸ فاز ۱۲ (BLK-001) |

**نتیجه: گیت فاز ۶ باز است و فاز ۷ (باسکول، انبار، سورتینگ، سبد، شراکت) می‌تواند شروع شود.**
