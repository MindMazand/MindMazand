#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
G4 - Mazandasht UI mockups (vector).

Emits one SVG per theme showing the management dashboard shell. SVG keeps the
Persian text as real text, so it renders correctly in any browser and can be
exported to PNG later.

Usage:
    python3 tools/build_mockups.py --out docs/visuals
"""
from __future__ import annotations

import argparse
from pathlib import Path

THEMES = {
    "light": {
        "bg": "#F3F3F3", "header": "#E9F2EC", "sidebar": "#FFFFFF",
        "card": "#FFFFFF", "border": "#D7E2DA", "text": "#17211B",
        "muted": "#5F6B63", "green": "#1B5E3A", "accent": "#2E7D5B",
        "orange": "#C75A08", "grid": "#E4EBE6",
    },
    "dark": {
        "bg": "#0B1711", "header": "#0B2618", "sidebar": "#0E1A13",
        "card": "#16241C", "border": "#2C4636", "text": "#EAF4ED",
        "muted": "#A9B9AE", "green": "#54B50D", "accent": "#D46909",
        "orange": "#D46909", "grid": "#1E2E24",
    },
}

W, H = 1600, 1000
NAV = [
    ("داشبورد مدیریتی", True),
    ("ثبت سریع عملیات", False),
    ("بررسی و تأیید اسناد", False),
    ("باسکول و ورود بار", False),
    ("سورتینگ و انبار", False),
    ("خرید و فروش", False),
    ("حسابداری و مالی", False),
    ("بانک و چک", False),
    ("سبد و امانی", False),
    ("اشخاص و کارکنان", False),
    ("گزارش‌های تحلیلی", False),
    ("ورود اطلاعات Excel / CSV", False),
    ("اطلاعات پایه و تنظیمات", False),
]
KPIS = [
    ("ورودی امروز (kg)", "۱۲٬۴۸۰", "green"),
    ("خروجی امروز (kg)", "۹٬۳۱۵", "orange"),
    ("فروش امروز", "۱٬۲۴۰٬۰۰۰", "green"),
    ("مانده اشخاص", "۳۸۶٬۵۰۰", "accent"),
]
BARS_IN = [60, 78, 52, 90, 70, 84, 66]
BARS_OUT = [40, 58, 44, 70, 50, 62, 48]
DAYS = ["ش", "ی", "د", "س", "چ", "پ", "ج"]
TABLE = [
    ("W-1404-000128", "باسکول ورودی", "پرتقال", "۴٬۲۰۰", "تأییدشده"),
    ("S-1404-000045", "سورتینگ", "پرتقال درجه یک", "۳٬۸۰۰", "بررسی"),
    ("P-1404-000011", "فاکتور خرید", "شرکت نمونه", "۱۲٬۰۰۰", "تأییدشده"),
    ("B-1404-000007", "تراکنش بانکی", "بانک صادرات", "۵۰۰٬۰۰۰", "تأییدشده"),
]


def esc(text: str) -> str:
    return text.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


def svg(theme: str) -> str:
    c = THEMES[theme]
    out = [f'<svg xmlns="http://www.w3.org/2000/svg" width="{W}" height="{H}" '
           f'viewBox="0 0 {W} {H}" font-family="Vazirmatn, Segoe UI, Tahoma">']
    out.append(f'<rect width="{W}" height="{H}" fill="{c["bg"]}"/>')

    # header
    out.append(f'<rect x="0" y="0" width="{W}" height="88" fill="{c["header"]}"/>')
    out.append(f'<line x1="0" y1="88" x2="{W}" y2="88" stroke="{c["border"]}" stroke-width="1"/>')
    out.append(f'<circle cx="1520" cy="44" r="26" fill="{c["green"]}"/>')
    out.append(f'<text x="1470" y="52" font-size="24" font-weight="bold" fill="{c["text"]}" text-anchor="end">سامانه یکپارچه مازندشت</text>')
    out.append(f'<text x="1470" y="74" font-size="13" fill="{c["muted"]}" text-anchor="end">مدیریت عملیات، بازرگانی و حسابداری</text>')
    for i, label in enumerate(["آفلاین | پایگاه داده محلی", "۱۴۰۴/۰۸/۲۹"]):
        w = 190 if i == 0 else 130
        x = 40 + i * (w + 12)
        out.append(f'<rect x="{x}" y="26" width="{w}" height="36" rx="18" fill="{c["card"]}" stroke="{c["border"]}"/>')
        out.append(f'<text x="{x + w - 14}" y="49" font-size="13" fill="{c["muted"]}" text-anchor="end">{esc(label)}</text>')

    # sidebar (right side, RTL)
    sx = W - 300
    out.append(f'<rect x="{sx}" y="88" width="300" height="{H - 88}" fill="{c["sidebar"]}"/>')
    out.append(f'<line x1="{sx}" y1="88" x2="{sx}" y2="{H}" stroke="{c["border"]}" stroke-width="1"/>')
    y = 116
    for label, active in NAV:
        if active:
            out.append(f'<rect x="{sx + 16}" y="{y - 20}" width="268" height="40" rx="10" fill="{c["green"]}"/>')
            color = "#FFFFFF"
        else:
            color = c["text"]
        out.append(f'<text x="{sx + 268}" y="{y + 5}" font-size="14" fill="{color}" text-anchor="end">{esc(label)}</text>')
        y += 48

    # KPI cards
    card_w, card_h, gap = 292, 128, 16
    x0 = 40
    for i, (title, value, color) in enumerate(KPIS):
        x = x0 + i * (card_w + gap)
        out.append(f'<rect x="{x}" y="120" width="{card_w}" height="{card_h}" rx="16" fill="{c["card"]}" stroke="{c["border"]}"/>')
        out.append(f'<rect x="{x}" y="120" width="6" height="{card_h}" rx="3" fill="{c[color]}"/>')
        out.append(f'<text x="{x + card_w - 18}" y="158" font-size="14" fill="{c["muted"]}" text-anchor="end">{esc(title)}</text>')
        out.append(f'<text x="{x + card_w - 18}" y="208" font-size="26" font-weight="bold" fill="{c["text"]}" text-anchor="end">{esc(value)}</text>')

    # chart card
    cx, cy, cw, ch = 40, 272, 760, 380
    out.append(f'<rect x="{cx}" y="{cy}" width="{cw}" height="{ch}" rx="16" fill="{c["card"]}" stroke="{c["border"]}"/>')
    out.append(f'<text x="{cx + cw - 20}" y="{cy + 36}" font-size="16" font-weight="bold" fill="{c["text"]}" text-anchor="end">جریان هفت‌روزه باسکول</text>')
    out.append(f'<text x="{cx + 20}" y="{cy + 36}" font-size="12" fill="{c["green"]}">■ ورودی</text>')
    out.append(f'<text x="{cx + 110}" y="{cy + 36}" font-size="12" fill="{c["orange"]}">■ خروجی</text>')
    base = cy + ch - 50
    plot_h = 240
    for i in range(7):
        bx = cx + 60 + i * 100
        ih = int(BARS_IN[i] / 100 * plot_h)
        oh = int(BARS_OUT[i] / 100 * plot_h)
        out.append(f'<rect x="{bx - 18}" y="{base - ih}" width="16" height="{ih}" rx="4" fill="{c["green"]}"/>')
        out.append(f'<rect x="{bx + 4}" y="{base - oh}" width="16" height="{oh}" rx="4" fill="{c["orange"]}"/>')
        out.append(f'<text x="{bx}" y="{base + 26}" font-size="13" fill="{c["muted"]}" text-anchor="middle">{DAYS[i]}</text>')
    for g in range(5):
        gy = base - int(g / 4 * plot_h)
        out.append(f'<line x1="{cx + 40}" y1="{gy}" x2="{cx + cw - 30}" y2="{gy}" stroke="{c["grid"]}" stroke-width="1"/>')

    # table card
    tx, ty, tw, th = 824, 272, 436, 380
    out.append(f'<rect x="{tx}" y="{ty}" width="{tw}" height="{th}" rx="16" fill="{c["card"]}" stroke="{c["border"]}"/>')
    out.append(f'<text x="{tx + tw - 20}" y="{ty + 36}" font-size="16" font-weight="bold" fill="{c["text"]}" text-anchor="end">آخرین اسناد</text>')
    headers = ["شماره", "نوع", "موضوع", "مقدار", "وضعیت"]
    widths = [78, 84, 104, 80, 66]
    hx = tx + tw - 20
    header_cells = []
    for h, w in zip(headers, widths):
        header_cells.append((hx - w, h))
        hx -= w
    for (hxx, h) in header_cells:
        out.append(f'<text x="{hxx + 6}" y="{ty + 76}" font-size="12" fill="{c["muted"]}">{esc(h)}</text>')
    ry = ty + 108
    for row in TABLE:
        hx = tx + tw - 20
        for value, w in zip(row, widths):
            out.append(f'<text x="{hx - 6}" y="{ry}" font-size="12" fill="{c["text"]}" text-anchor="end">{esc(value)}</text>')
            hx -= w
        ry += 40
        out.append(f'<line x1="{tx + 18}" y1="{ry - 18}" x2="{tx + tw - 18}" y2="{ry - 18}" stroke="{c["grid"]}"/>')

    # footer
    out.append(f'<text x="40" y="{H - 24}" font-size="12" fill="{c["muted"]}">مازندشت ۰٫۵٫۲-alpha3 | تم: {"روشن" if theme == "light" else "تیره"}</text>')
    out.append("</svg>")
    return "\n".join(out) + "\n"


def design_system(theme: str) -> str:
    """Component library: tokens and controls rendered from the same palette."""
    c = THEMES[theme]
    W2, H2 = 1200, 900
    out = [f'<svg xmlns="http://www.w3.org/2000/svg" width="{W2}" height="{H2}" '
           f'viewBox="0 0 {W2} {H2}" font-family="Vazirmatn, Segoe UI, Tahoma">']
    out.append(f'<rect width="{W2}" height="{H2}" fill="{c["bg"]}"/>')
    out.append(f'<text x="{W2 - 30}" y="52" font-size="24" font-weight="bold" fill="{c["text"]}" text-anchor="end">کتابخانه اجزای رابط کاربری مازندشت</text>')

    # palette
    swatches = [("BrandGreen", c["green"]), ("Accent", c["accent"]), ("BrandOrange", c["orange"]),
                ("Text", c["text"]), ("MutedText", c["muted"]), ("Danger", "#B3261E"),
                ("Success", "#2E7D32"), ("Warning", "#B26A00")]
    x = 60
    for name, color in swatches:
        out.append(f'<rect x="{x}" y="86" width="110" height="60" rx="10" fill="{color}"/>')
        out.append(f'<text x="{x + 55}" y="168" font-size="11" fill="{c["muted"]}" text-anchor="middle">{name}</text>')
        x += 128

    # buttons
    out.append(f'<text x="{W2 - 30}" y="232" font-size="16" font-weight="bold" fill="{c["text"]}" text-anchor="end">دکمه‌ها</text>')
    for i, (label, fill, fg) in enumerate([("دکمه اصلی", c["accent"], "#FFFFFF"),
                                          ("دکمه شیشه‌ای", c["card"], c["text"]),
                                          ("دکمه مخرب", "#B3261E", "#FFFFFF")]):
        bx = 60 + i * 210
        out.append(f'<rect x="{bx}" y="250" width="180" height="48" rx="12" fill="{fill}" stroke="{c["border"]}"/>')
        out.append(f'<text x="{bx + 90}" y="280" font-size="14" fill="{fg}" text-anchor="middle">{esc(label)}</text>')

    # form fields
    out.append(f'<text x="{W2 - 30}" y="372" font-size="16" font-weight="bold" fill="{c["text"]}" text-anchor="end">فیلدهای ورودی</text>')
    for i, (label, value) in enumerate([("کد محصول", "PR-1024"), ("نام محصول", "پرتقال واشینگتن")]):
        fx = 60 + i * 420
        out.append(f'<rect x="{fx}" y="390" width="380" height="76" rx="12" fill="{c["card"]}" stroke="{c["border"]}"/>')
        out.append(f'<text x="{fx + 360}" y="418" font-size="12" fill="{c["muted"]}" text-anchor="end">{esc(label)}</text>')
        out.append(f'<text x="{fx + 360}" y="448" font-size="16" fill="{c["text"]}" text-anchor="end">{esc(value)}</text>')

    # status badges
    out.append(f'<text x="{W2 - 30}" y="532" font-size="16" font-weight="bold" fill="{c["text"]}" text-anchor="end">برچسب‌های وضعیت</text>')
    for i, (label, color) in enumerate([("پیش‌نویس", c["muted"]), ("در بررسی", "#B26A00"),
                                        ("تأییدشده", "#2E7D32"), ("باطل‌شده", "#B3261E")]):
        bx = 60 + i * 210
        out.append(f'<rect x="{bx}" y="550" width="180" height="40" rx="20" fill="{c["card"]}" stroke="{color}"/>')
        out.append(f'<circle cx="{bx + 160}" cy="570" r="5" fill="{color}"/>')
        out.append(f'<text x="{bx + 120}" y="576" font-size="13" fill="{c["text"]}" text-anchor="middle">{esc(label)}</text>')

    # table sample
    out.append(f'<rect x="60" y="620" width="1080" height="250" rx="16" fill="{c["card"]}" stroke="{c["border"]}"/>')
    out.append(f'<text x="1120" y="656" font-size="15" font-weight="bold" fill="{c["text"]}" text-anchor="end">نمونه جدول اسناد</text>')
    headers = ["شماره سند", "نوع", "تاریخ", "مقدار", "وضعیت"]
    widths = [150, 150, 130, 160, 150]
    hx = 1120
    cells = []
    for h, w in zip(headers, widths):
        cells.append((hx - w, h))
        hx -= w
    for (hxx, h) in cells:
        out.append(f'<text x="{hxx + 10}" y="692" font-size="12" fill="{c["muted"]}">{esc(h)}</text>')
    rows = [("W-1404-000128", "باسکول ورودی", "۱۴۰۴/۰۸/۲۹", "۴٬۲۰۰ kg", "تأییدشده"),
            ("S-1404-000045", "سورتینگ", "۱۴۰۴/۰۸/۲۹", "۳٬۸۰۰ kg", "در بررسی")]
    ry = 726
    for row in rows:
        hx = 1120
        for value, w in zip(row, widths):
            out.append(f'<text x="{hx - 10}" y="{ry}" font-size="13" fill="{c["text"]}" text-anchor="end">{esc(value)}</text>')
            hx -= w
        ry += 44
        out.append(f'<line x1="80" y1="{ry - 22}" x2="1120" y2="{ry - 22}" stroke="{c["grid"]}"/>')
    out.append("</svg>")
    return "\n".join(out) + "\n"


def main() -> int:
    parser = argparse.ArgumentParser(description="Build Mazandasht UI mockups")
    parser.add_argument("--out", required=True, type=Path)
    args = parser.parse_args()
    args.out.mkdir(parents=True, exist_ok=True)
    for theme in THEMES:
        for name, builder in (("ui-mockup", svg), ("ui-design-system", design_system)):
            path = args.out / f"{name}-{theme}.svg"
            path.write_text(builder(theme), encoding="utf-8")
            print(f"  wrote {path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
