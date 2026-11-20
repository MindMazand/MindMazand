#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
G1 - Mazandasht Excel template designer (generation 2).

Builds the eleven blank import templates from the canonical profile contract
(docs/11_Import_Profiles_Canonical.json) with a branded, accessible layout and
real in-sheet validation, in three themes:

    Templates/<profile>.xlsx              -> light, Windows default
    Templates/Themes/Dark/<profile>.xlsx  -> dark
    Templates/Themes/Caspian/<profile>.xlsx -> Caspian teal

The data sheet keeps the owner's contract intact: sheet names, the header row at
row 1, and no sample rows. Everything else (branding, colours, validation,
freeze panes, filters, column widths) is additive.

Usage:
    python3 tools/build_templates.py --templates app/Mazandasht.Desktop/Templates \
        --contract docs/11_Import_Profiles_Canonical.json \
        --logo app/Mazandasht.Desktop/Assets/mazandasht-logo.png
"""
from __future__ import annotations

import argparse
import json
import tempfile
from pathlib import Path

from openpyxl import Workbook
from openpyxl.formatting.rule import CellIsRule, FormulaRule
from openpyxl.styles import Alignment, Border, Font, PatternFill, Side
from openpyxl.utils import get_column_letter
from openpyxl.worksheet.datavalidation import DataValidation
from PIL import Image as PilImage

# --------------------------------------------------------------------------
# Module names and usage text: preserved verbatim from the owner's templates.
# --------------------------------------------------------------------------
MODULE_USAGE = {
    "01_Parties.xlsx": ("اشخاص",
        "هر شخص با کد یکتای پایدار وارد شود. کد حسابداری/مانده افتتاحیه از این قالب ایجاد نمی‌شود و باید جداگانه و با تأیید ثبت شود."),
    "02_Products.xlsx": ("محصولات",
        "کد محصول باید یکتا باشد. گروه و واحد باید از اطلاعات پایه تعریف‌شده در نرم‌افزار انتخاب شوند."),
    "03_Weighing_In.xlsx": ("باسکول ورودی",
        "وزن‌ها بر حسب کیلوگرم با حداکثر سه رقم اعشار وارد شوند. این قالب برای ورود بار است؛ خروج موجودی باید از فاکتور فروش یا سند انتقال ثبت شود."),
    "04_Purchase_Invoices.xlsx": ("فاکتور خرید",
        "هر ردیف خرید باید به کد باسکول/محموله معتبر متصل باشد. مبلغ‌ها ریال هستند و قیمت واحد ریال بر کیلوگرم است."),
    "05_Sales_Invoices.xlsx": ("فاکتور فروش",
        "فروش باید به محموله موجود و تأییدشده متصل باشد. قیمت‌ها و مبلغ‌ها به ریال ثبت می‌شوند."),
    "06_Sorting.xlsx": ("سورتینگ",
        "در هر گروه عملیات، مجموع ورودی باید با خروجی + ضایعات + افت در محدوده تلورانس تعریف‌شده برابر باشد. وزن‌ها کیلوگرم هستند."),
    "07_Basket_Movements.xlsx": ("گردش سبد",
        "گردش تعداد سبد از گردش پولی مستقل است. برگشت امانی فقط در محدوده مانده امانی تأییدشده مجاز خواهد بود."),
    "08_Bank_Transactions.xlsx": ("تراکنش بانکی",
        "مبالغ به ریال هستند. هر ردیف باید یک جهت مشخص داشته باشد؛ طرف حساب و زیرگروه درآمد/هزینه باید طبق قواعد ماژول تعیین شوند."),
    "09_Cheques.xlsx": ("چک",
        "شماره چک و شناسه صیادی به‌صورت متن وارد شوند تا صفرهای ابتدایی حذف نشوند."),
    "10_Settlements.xlsx": ("تخصیص تسویه",
        "این قالب برای اتصال سند پرداخت موجود به فاکتور موجود است؛ خودش تراکنش بانکی یا سند حسابداری جدید ایجاد نمی‌کند."),
    "11_Partnership.xlsx": ("عملیات شراکت",
        "وزن‌ها به کیلوگرم هستند. تعداد سبد مصرف‌شده برای تار و تعداد سبد برگشتی امانی دو فیلد مستقل‌اند."),
}

GENERAL_RULES = [
    "نام شیت داده‌ها و ردیف عنوان ستون‌ها را تغییر ندهید.",
    "ردیف اول شیت داده‌ها عنوان ستون‌ها است؛ داده‌ها را از ردیف دوم وارد کنید.",
    "کدها و شناسه‌ها را به‌صورت متن نگه دارید تا صفرهای ابتدایی حذف نشوند.",
    "تاریخ شمسی را به شکل YYYY/MM/DD وارد کنید؛ تاریخ میلادی اکسل را بدون تبدیل دستی جایگزین نکنید.",
    "وزن‌ها بر حسب کیلوگرم و با حداکثر سه رقم اعشار هستند.",
    "تمام مبالغ پولی بر حسب ریال هستند؛ تومان را در ستون ریال وارد نکنید.",
    "سلول خالی با صفر یکسان نیست؛ فیلد اختیاری را در صورت نامشخص بودن خالی بگذارید.",
    "از ادغام سلول‌ها، فرمول در ستون‌های ورودی و ردیف جمع کل در شیت داده‌ها استفاده نکنید.",
    "بارگذاری فایل به‌تنهایی اطلاعات را قطعی نمی‌کند؛ نرم‌افزار ابتدا پیش‌نمایش و کنترل خطا ارائه می‌دهد.",
    "هیچ مانده افتتاحیه، پرداخت یا سند حسابداری از روی حدس ساخته نمی‌شود.",
]

MERGE_RULES = [
    "برای ادغام چند فایل هم‌ماژول، ردیف‌ها را در یک قالب به ترتیب اضافه کنید؛ ستون‌ها و ترتیب آن‌ها باید یکسان بماند.",
    "کد یکتا (مثل کد شخص یا کد محصول) در کل فایل ادغام‌شده نباید تکرار شود؛ ابزار ادغام، ردیف‌های تکراری را پرچم‌گذاری می‌کند.",
    "برای اصلاح رکورد موجود، همان کد یکتا را نگه دارید و فقط فیلد موردنظر را تغییر دهید؛ نرم‌افزار به‌جای درج دوباره، رکورد را به‌روز می‌کند.",
    "پیش از ادغام، نسخه پشتیبان از فایل خود نگه دارید.",
]

THEMES = {
    "Light": {
        "page": "FFFFFF", "surface": "F4F8F5", "surface_alt": "EAF2EC",
        "primary": "1B5E3A", "primary_soft": "D9E9DF", "accent": "C9A227",
        "text": "17211B", "text_soft": "4E6156", "border": "C9DAD0",
        "header_text": "FFFFFF", "danger": "B3261E", "danger_soft": "F6DAD7",
        "guide_title": "123B28",
    },
    "Dark": {
        "page": "12171A", "surface": "1B2226", "surface_alt": "222B30",
        "primary": "2E7D5B", "primary_soft": "243830", "accent": "E0B84A",
        "text": "E8EEEC", "text_soft": "9FB0A8", "border": "33403A",
        "header_text": "0C1013", "danger": "FF8A80", "danger_soft": "3A2220",
        "guide_title": "8FD3B4",
    },
    "Caspian": {
        "page": "FFFFFF", "surface": "F2F7F8", "surface_alt": "E6EFF1",
        "primary": "0E6E7E", "primary_soft": "D3E7EB", "accent": "D97706",
        "text": "10242A", "text_soft": "4A6A72", "border": "BED8DD",
        "header_text": "FFFFFF", "danger": "B3261E", "danger_soft": "F6DAD7",
        "guide_title": "0A4A55",
    },
}

DATA_SHEET = "داده‌ها"
DICT_SHEET = "فرهنگ داده"
GUIDE_SHEET = "راهنما"


def _fill(color: str) -> PatternFill:
    return PatternFill("solid", fgColor=color)


def _thin(color: str) -> Side:
    return Side(style="thin", color=color)


def _border(color: str) -> Border:
    s = _thin(color)
    return Border(left=s, right=s, top=s, bottom=s)


def _field_kind(field: str, fmt: str | None) -> str:
    """Classify a field for number format / validation."""
    text = (field or "") + " " + (fmt or "")
    if "IN / OUT" in text or "IN/OUT" in text:
        return "direction"
    if "YYYY/MM/DD" in text:
        return "date"
    if "کیلوگرم" in text or "(kg)" in text:
        return "weight"
    if "ریال" in text:
        return "money"
    if "درصد" in text or "%" in text:
        return "percent"
    if "تعداد" in text:
        return "count"
    if "متن یکتا" in (fmt or ""):
        return "unique_text"
    if "متن" in (fmt or ""):
        return "text"
    return "text"


def _style_guide(ws, theme: dict, module: str, usage: str, fields: list[dict]) -> None:
    t = THEMES[theme]
    ws.sheet_view.showGridLines = False
    ws.column_dimensions["A"].width = 22
    ws.column_dimensions["B"].width = 30
    ws.column_dimensions["C"].width = 26
    ws.column_dimensions["D"].width = 60

    ws["A1"] = "مازندشت"
    ws["A1"].font = Font(name="B Nazanin", size=26, bold=True, color=t["guide_title"])
    ws.merge_cells("A1:D1")
    ws["A2"] = f"قالب ورود اطلاعات Excel — ماژول {module}"
    ws["A2"].font = Font(name="B Nazanin", size=14, bold=True, color=t["primary"])
    ws.merge_cells("A2:D2")
    ws["A3"] = "نسخه قالب: ۲ | ساختار داده: مازندشت ۰.۵.۲+"
    ws["A3"].font = Font(name="B Nazanin", size=10, color=t["text_soft"])
    ws.merge_cells("A3:D3")

    row = 5

    def section(title: str) -> int:
        nonlocal row
        ws.cell(row, 1, title)
        ws.cell(row, 1).font = Font(name="B Nazanin", size=12, bold=True, color=t["header_text"])
        ws.cell(row, 1).fill = _fill(t["primary"])
        ws.cell(row, 1).alignment = Alignment(vertical="center", horizontal="right")
        ws.merge_cells(start_row=row, start_column=1, end_row=row, end_column=4)
        ws.row_dimensions[row].height = 24
        row += 1
        return row

    def bullet(text: str, bold: bool = False) -> None:
        nonlocal row
        cell = ws.cell(row, 1, text)
        cell.font = Font(name="B Nazanin", size=10, bold=bold, color=t["text"])
        cell.alignment = Alignment(vertical="center", horizontal="right", wrap_text=True)
        ws.merge_cells(start_row=row, start_column=1, end_row=row, end_column=4)
        row += 1

    section("کاربرد")
    bullet(usage)
    row += 1

    section("قواعد عمومی")
    for rule in GENERAL_RULES:
        bullet("• " + rule)
    row += 1

    section("ادغام و اصلاح فایل")
    for rule in MERGE_RULES:
        bullet("• " + rule)
    row += 1

    section("فرهنگ ستون‌های داده")
    headers = ["عنوان ستون", "الزامی", "قالب / واحد", "نوع اعتبارسنجی"]
    for col, text in enumerate(headers, start=1):
        c = ws.cell(row, col, text)
        c.font = Font(name="B Nazanin", size=10, bold=True, color=t["header_text"])
        c.fill = _fill(t["primary_soft"] if theme != "Dark" else t["primary_soft"])
        c.border = _border(t["border"])
        c.alignment = Alignment(horizontal="center", vertical="center")
    row += 1
    for f in fields:
        kind = _field_kind(f["field"], f.get("format"))
        kind_fa = {
            "direction": "انتخاب از فهرست (IN/OUT)", "date": "تاریخ شمسی YYYY/MM/DD",
            "weight": "عدد ≥ 0 (کیلوگرم)", "money": "عدد صحیح ≥ 0 (ریال)",
            "percent": "عدد ۰ تا ۱۰۰ (درصد)", "count": "عدد صحیح ≥ 0",
            "unique_text": "متن یکتا (بدون تکرار)", "text": "متن آزاد",
        }.get(kind, "متن آزاد")
        values = [f["field"], "بله" if f["required"] else "خیر", f.get("format") or "—", kind_fa]
        for col, value in enumerate(values, start=1):
            c = ws.cell(row, col, value)
            c.font = Font(name="B Nazanin", size=10, color=t["text"])
            c.border = _border(t["border"])
            c.alignment = Alignment(horizontal="right" if col == 1 else "center", vertical="center")
            if f["required"]:
                c.fill = _fill(t["surface_alt"])
        row += 1

    row += 1
    section("راهنمای رنگ")
    bullet("سطرهای با پس‌زمینه روشن در فرهنگ ستون‌ها، فیلدهای الزامی هستند.")
    bullet("ستون‌های عنوان دارای ستاره (*) فیلد الزامی است؛ فیلدهای اختیاری مقدار پیش‌فرض نمی‌گیرند.")


def _style_data_sheet(ws, theme: dict, fields: list[dict]) -> None:
    t = THEMES[theme]
    headers = [f["field"] for f in fields]
    ws.sheet_view.showGridLines = theme != "Dark"

    for idx, field in enumerate(fields, start=1):
        kind = _field_kind(field["field"], field.get("format"))
        # NOTE: the header text must stay exactly equal to the dictionary field
        # name - tests/test_stage05_data_entry_and_reset.py asserts it.
        title = field["field"]
        c = ws.cell(1, idx, title)
        c.font = Font(name="B Nazanin", size=11, bold=True, color=t["header_text"])
        c.fill = _fill(t["primary"] if field["required"] else t["primary_soft"])
        if not field["required"]:
            c.font = Font(name="B Nazanin", size=11, bold=True, color=t["text"])
        c.alignment = Alignment(horizontal="center", vertical="center", wrap_text=True)
        c.border = _border(t["border"])
        letter = get_column_letter(idx)
        width = max(12, min(30, len(field["field"]) + 6))
        ws.column_dimensions[letter].width = width
    ws.row_dimensions[1].height = 34
    ws.freeze_panes = "A2"
    ws.auto_filter.ref = f"A1:{get_column_letter(len(fields))}1"

    last_col = get_column_letter(len(fields))
    # The intake contract allows at most 10,000 data rows; bound the styling to
    # that range so the workbook stays light.
    last_row = 10000

    # Direction columns: drop-down list.
    for idx, field in enumerate(fields, start=1):
        if _field_kind(field["field"], field.get("format")) == "direction":
            letter = get_column_letter(idx)
            dv = DataValidation(type="list", formula1='"IN,OUT,NEUTRAL"', allow_blank=True)
            dv.error = "فقط IN یا OUT یا NEUTRAL مجاز است."
            dv.errorTitle = "جهت نامعتبر"
            ws.add_data_validation(dv)
            dv.add(f"{letter}2:{letter}{last_row}")

    # Unique text columns: highlight duplicates; numeric columns: soft guide.
    for idx, field in enumerate(fields, start=1):
        kind = _field_kind(field["field"], field.get("format"))
        letter = get_column_letter(idx)
        if kind == "unique_text":
            ws.conditional_formatting.add(
                f"{letter}2:{letter}{last_row}",
                FormulaRule(formula=[f"COUNTIF(${letter}$2:${letter}${last_row},{letter}2)>1"],
                            fill=_fill(t["danger_soft"]),
                            font=Font(color=t["danger"], bold=True)))
        elif kind in ("weight", "money", "percent", "count"):
            ws.conditional_formatting.add(
                f"{letter}2:{letter}{last_row}",
                CellIsRule(operator="greaterThanOrEqual", formula=["0"], fill=_fill(t["surface_alt"])))

    # Number formats for readability (bounded range).
    for idx, field in enumerate(fields, start=1):
        kind = _field_kind(field["field"], field.get("format"))
        letter = get_column_letter(idx)
        if kind == "weight":
            fmt = "#,##0.000"
        elif kind in ("money", "count"):
            fmt = "#,##0"
        elif kind == "percent":
            fmt = "0.00"
        elif kind in ("unique_text", "date", "text", "direction"):
            fmt = "@"
        else:
            fmt = "General"
        for row in range(2, min(last_row, 1000) + 1):
            ws.cell(row, idx).number_format = fmt

    # Example-free: the sheet intentionally carries no data rows.


def _style_dictionary(ws, theme: dict, fields: list[dict]) -> None:
    t = THEMES[theme]
    ws.sheet_view.showGridLines = False
    headers = ["عنوان ستون", "الزامی", "قالب / واحد", "شرح و قاعده", "نوع اعتبارسنجی"]
    widths = [26, 10, 22, 58, 30]
    for idx, (text, width) in enumerate(zip(headers, widths), start=1):
        c = ws.cell(1, idx, text)
        c.font = Font(name="B Nazanin", size=11, bold=True, color=t["header_text"])
        c.fill = _fill(t["primary"])
        c.alignment = Alignment(horizontal="center", vertical="center")
        c.border = _border(t["border"])
        ws.column_dimensions[get_column_letter(idx)].width = width
    ws.row_dimensions[1].height = 26
    ws.freeze_panes = "A2"
    ws.auto_filter.ref = f"A1:E{len(fields) + 1}"

    for r, f in enumerate(fields, start=2):
        kind = _field_kind(f["field"], f.get("format"))
        kind_fa = {
            "direction": "IN/OUT/NEUTRAL", "date": "YYYY/MM/DD", "weight": "kg",
            "money": "ریال", "percent": "درصد", "count": "عدد صحیح",
            "unique_text": "یکتا", "text": "متن",
        }.get(kind, "متن")
        values = [f["field"], "بله" if f["required"] else "خیر", f.get("format") or "—",
                  f.get("rule") or "—", kind_fa]
        for col, value in enumerate(values, start=1):
            c = ws.cell(r, col, value)
            c.font = Font(name="B Nazanin", size=10, color=t["text"])
            c.border = _border(t["border"])
            c.alignment = Alignment(horizontal="right" if col in (1, 4) else "center",
                                    vertical="center", wrap_text=(col == 4))
            if f["required"]:
                c.fill = _fill(t["surface_alt"])
        ws.row_dimensions[r].height = 20


def _prepare_logo(logo_path: Path | None, work_dir: Path) -> Path | None:
    """Resize the brand logo once so every workbook embeds a light copy."""
    if not logo_path or not logo_path.exists():
        return None
    try:
        target = work_dir / "mazandasht-logo-96.png"
        with PilImage.open(logo_path) as img:
            img = img.convert("RGBA")
            img.thumbnail((140, 140))
            img.save(target)
        return target
    except Exception as exc:  # pragma: no cover - logo is decorative
        print(f"  logo skipped: {exc}")
        return None


def _embed_logo(wb: Workbook, prepared_logo: Path | None) -> None:
    if not prepared_logo:
        return
    try:
        from openpyxl.drawing.image import Image as XlImage
        logo = XlImage(str(prepared_logo))
        logo.width, logo.height = 96, 96
        wb[GUIDE_SHEET].add_image(logo, "F1")
    except Exception as exc:  # pragma: no cover - logo is decorative
        print(f"  logo skipped: {exc}")


def build_profile(profile: dict, theme_name: str, out_dir: Path, prepared_logo: Path | None) -> Path:
    module, usage = MODULE_USAGE[profile["file"]]
    fields = profile["dictionary"]
    wb = Workbook()
    guide = wb.active
    guide.title = GUIDE_SHEET
    data = wb.create_sheet(DATA_SHEET)
    dictionary = wb.create_sheet(DICT_SHEET)

    _style_guide(guide, theme_name, module, usage, fields)
    _style_data_sheet(data, theme_name, fields)
    _style_dictionary(dictionary, theme_name, fields)
    _embed_logo(wb, prepared_logo)

    out_dir.mkdir(parents=True, exist_ok=True)
    out_path = out_dir / profile["file"]
    wb.save(out_path)
    return out_path


def main() -> int:
    parser = argparse.ArgumentParser(description="Build Mazandasht Excel templates")
    parser.add_argument("--templates", required=True, type=Path)
    parser.add_argument("--contract", required=True, type=Path)
    parser.add_argument("--logo", type=Path, default=None)
    parser.add_argument("--themes", default="Light,Dark,Caspian")
    args = parser.parse_args()

    contract = json.loads(args.contract.read_text(encoding="utf-8"))
    profiles = contract["profiles"]
    themes = [t.strip() for t in args.themes.split(",") if t.strip()]

    written = 0
    with tempfile.TemporaryDirectory(prefix="mazandasht-logo-") as tmp:
        prepared_logo = _prepare_logo(args.logo, Path(tmp))
        for theme in themes:
            if theme == "Light":
                out_dir = args.templates
            else:
                out_dir = args.templates / "Themes" / theme
            for profile in profiles:
                path = build_profile(profile, theme, out_dir, prepared_logo)
                written += 1
                print(f"  wrote {path}")
    print(f"\nbuilt {written} workbooks across {len(themes)} themes")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
