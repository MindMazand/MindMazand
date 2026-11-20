# نصب‌کنندهٔ مازندشت (G12)

این پوشه اسکریپت نصب‌کنندهٔ **Inno Setup 6** را دارد. نصب‌کننده در محیط لینوکسِ توسعه
ساخته نمی‌شود؛ روی ویندوز اجرا می‌شود.

## پیش‌نیازها (روی ویندوز)

| ابزار | نسخه | کار |
|---|---|---|
| Visual Studio 2022 | با بارگذاری «دسکتاپ .NET» | ساخت WPF |
| .NET Framework 4.8 Developer Pack | 4.8 | هدف پروژه |
| Inno Setup 6 | 6.x | ساخت نصب‌کننده |
| Python 3 | 3.11+ | اجرای اسکریپت تأیید خروجی (اختیاری اما توصیه‌شده) |

## ساخت

```bat
build-windows.bat
```

این اسکریپت مراحل زیر را انجام می‌دهد:

1. `msbuild app\Mazandasht.sln /restore /p:Configuration=Release /p:Platform=x64`
2. `python tools\verify_windows_build.py app\Mazandasht.Desktop\bin\x64\Release`
3. `ISCC.exe installer\Mazandasht_Setup.iss` (اگر Inno Setup نصب باشد)

خروجی‌ها:

| مسیر | محتوا |
|---|---|
| `app\Mazandasht.Desktop\bin\x64\Release\` | اجرایی و فایل‌های کناری |
| `installer\Output\Mazandasht_Setup.exe` | نصب‌کننده |

اگر `ISCC_PATH` تعریف شده باشد، همان مسیر استفاده می‌شود؛ در غیر این صورت مسیر پیش‌فرض
نصب Inno Setup 6 جستجو می‌شود.

## ویژگی‌های نصب‌کننده

- **نصب برای کاربر جاری** (`PrivilegesRequired=lowest`)، در `%LOCALAPPDATA%\Mazandasht`؛
  بدون نیاز به دسترسی مدیر، چون پایگاه‌داده هم در `%LOCALAPPDATA%\Mazandasht\Data` زندگی می‌کند.
- **فقط x64** (`ArchitecturesAllowed=x64` و `ArchitecturesInstallIn64BitMode=x64`).
- **حداقل سیستم‌عامل:** ویندوز ۸٫۱ (`MinVersion=6.3.9600`).
- کپی `x64\SQLite.Interop.dll` (بدون آن `System.Data.SQLite` اجرا نمی‌شود).
- ساخت پوشه‌های `Data`، `Backups`، `Exports` با دسترسی نوشتن کاربر.
- میان‌بر میزکار و منوی شروع (قابل انتخاب در حین نصب).
- **حذف نصب هرگز پایگاه‌داده را پاک نمی‌کند**، چون پایگاه‌داده بیرون از پوشهٔ نصب است.

## نسخهٔ ادمینی (اختیاری)

اگر مالک بخواهد برنامه برای همهٔ کاربران روی یک دستگاه نصب شود، در `Mazandasht_Setup.iss`:

```
DefaultDirName={autopf}\Mazandasht
PrivilegesRequired=admin
```

این حالت دسترسی مدیر می‌خواهد و توصیهٔ پیش‌فرض نیست.

## چیزی که این نصب‌کننده تضمین نمی‌کند

- عدم نیاز به اینترنت در زمان نصب (نه چیزی دانلود می‌شود).
- نصب خودکار .NET Framework 4.8؛ اگر سیستم‌هدف نداشته باشد، باید جداگانه نصب شود.
- امضای دیجیتالی نصب‌کننده (برای آن به گواهی کدسنی نیاز است).
