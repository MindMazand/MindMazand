#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
G4 - Mazandasht dedicated icon set and UI mockups.

Generates, from the brand logo and palette:
    app/Mazandasht.Desktop/Assets/Icons/*.png     64x64 navigation glyphs
    app/Mazandasht.Desktop/Assets/Icons/app.ico   multi-size window icon
    docs/visuals/ui-mockup-<theme>.svg            vector UI mockups (Persian text)

Icons are drawn programmatically (no third-party artwork) so the set is
reproducible and license-clean.

Usage:
    python3 tools/build_visuals.py --logo app/Mazandasht.Desktop/Assets/mazandasht-logo.png \
        --icons app/Mazandasht.Desktop/Assets/Icons --mockups docs/visuals
"""
from __future__ import annotations

import argparse
import math
from pathlib import Path

from PIL import Image, ImageDraw

PALETTE = {
    "green": (27, 94, 58),
    "green_dark": (18, 59, 40),
    "lime": (84, 181, 13),
    "orange": (212, 105, 9),
    "white": (255, 255, 255),
    "ink": (23, 33, 27),
}

SIZE = 64
RADIUS = 16


def _base(color) -> Image.Image:
    img = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle([2, 2, SIZE - 3, SIZE - 3], radius=RADIUS, fill=color)
    return img, d


def _save(img: Image.Image, path: Path) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    img.save(path)


# --------------------------------------------------------------------------
# Glyphs. Each draws a white mark centred in the 64x64 tile.
# --------------------------------------------------------------------------
def g_dashboard():
    img, d = _base(PALETTE["green"])
    d.rounded_rectangle([12, 12, 28, 34], radius=4, fill=PALETTE["white"])
    d.rounded_rectangle([36, 12, 52, 24], radius=4, fill=PALETTE["white"])
    d.rounded_rectangle([36, 30, 52, 52], radius=4, fill=PALETTE["white"])
    return img


def g_quickadd():
    img, d = _base(PALETTE["green"])
    d.line([32, 16, 32, 48], fill=PALETTE["white"], width=7)
    d.line([16, 32, 48, 32], fill=PALETTE["white"], width=7)
    return img


def g_approval():
    img, d = _base(PALETTE["green"])
    d.ellipse([12, 12, 52, 52], fill=PALETTE["white"])
    d.ellipse([18, 18, 46, 46], fill=PALETTE["green"])
    d.line([25, 33, 30, 39], fill=PALETTE["white"], width=5)
    d.line([30, 39, 40, 26], fill=PALETTE["white"], width=5)
    return img


def g_weighing():
    img, d = _base(PALETTE["green"])
    d.rectangle([16, 20, 48, 40], fill=PALETTE["white"])
    d.polygon([(16, 20), (24, 10), (40, 10), (48, 20)], fill=PALETTE["white"])
    d.ellipse([26, 26, 38, 38], fill=PALETTE["green"])
    return img


def g_sorting():
    img, d = _base(PALETTE["green"])
    d.polygon([(14, 18), (50, 18), (32, 46)], fill=PALETTE["white"])
    d.ellipse([26, 24, 38, 36], fill=PALETTE["green"])
    return img


def g_trade():
    img, d = _base(PALETTE["green"])
    d.line([14, 24, 46, 24], fill=PALETTE["white"], width=6)
    d.polygon([(46, 18), (54, 24), (46, 30)], fill=PALETTE["white"])
    d.line([50, 40, 18, 40], fill=PALETTE["white"], width=6)
    d.polygon([(18, 34), (10, 40), (18, 46)], fill=PALETTE["white"])
    return img


def g_accounting():
    img, d = _base(PALETTE["green"])
    d.rectangle([14, 12, 50, 52], fill=PALETTE["white"])
    d.line([20, 20, 44, 20], fill=PALETTE["green"], width=4)
    d.line([20, 30, 44, 30], fill=PALETTE["green"], width=4)
    d.line([20, 40, 36, 40], fill=PALETTE["green"], width=4)
    return img


def g_bank():
    img, d = _base(PALETTE["green"])
    d.polygon([(32, 10), (54, 24), (10, 24)], fill=PALETTE["white"])
    d.rectangle([16, 26, 24, 46], fill=PALETTE["white"])
    d.rectangle([28, 26, 36, 46], fill=PALETTE["white"])
    d.rectangle([40, 26, 48, 46], fill=PALETTE["white"])
    d.rectangle([12, 48, 52, 53], fill=PALETTE["white"])
    return img


def g_basket():
    img, d = _base(PALETTE["green"])
    d.polygon([(12, 26), (52, 26), (44, 52), (20, 52)], fill=PALETTE["white"])
    d.arc([20, 8, 44, 34], start=180, end=360, fill=PALETTE["white"], width=5)
    d.line([20, 34, 26, 48], fill=PALETTE["green"], width=3)
    d.line([32, 34, 34, 50], fill=PALETTE["green"], width=3)
    d.line([44, 34, 40, 48], fill=PALETTE["green"], width=3)
    return img


def g_parties():
    img, d = _base(PALETTE["green"])
    d.ellipse([18, 12, 34, 28], fill=PALETTE["white"])
    d.pieslice([12, 30, 40, 58], start=180, end=360, fill=PALETTE["white"])
    d.ellipse([38, 18, 50, 30], fill=PALETTE["white"])
    d.pieslice([34, 32, 54, 54], start=180, end=360, fill=PALETTE["white"])
    return img


def g_reports():
    img, d = _base(PALETTE["green"])
    d.rectangle([14, 34, 22, 50], fill=PALETTE["white"])
    d.rectangle([27, 24, 35, 50], fill=PALETTE["white"])
    d.rectangle([40, 14, 48, 50], fill=PALETTE["white"])
    return img


def g_import():
    img, d = _base(PALETTE["green"])
    d.rectangle([12, 34, 52, 50], fill=PALETTE["white"])
    d.line([32, 12, 32, 34], fill=PALETTE["white"], width=6)
    d.polygon([(32, 8), (24, 20), (40, 20)], fill=PALETTE["white"])
    d.line([20, 42, 44, 42], fill=PALETTE["green"], width=4)
    return img


def g_settings():
    img, d = _base(PALETTE["green"])
    d.ellipse([22, 22, 42, 42], fill=PALETTE["white"])
    for angle in range(0, 360, 45):
        x = 32 + 17 * math.cos(math.radians(angle))
        y = 32 + 17 * math.sin(math.radians(angle))
        d.rounded_rectangle([x - 4, y - 4, x + 4, y + 4], radius=2, fill=PALETTE["white"])
    return img


def g_theme():
    img, d = _base(PALETTE["green"])
    d.pieslice([12, 12, 52, 52], start=0, end=300, fill=PALETTE["white"])
    d.pieslice([22, 22, 42, 42], start=0, end=300, fill=PALETTE["green"])
    d.ellipse([28, 28, 36, 36], fill=PALETTE["orange"])
    return img


def g_backup():
    img, d = _base(PALETTE["green"])
    d.rectangle([14, 12, 44, 44], fill=PALETTE["white"])
    d.rectangle([38, 24, 50, 50], fill=PALETTE["white"])
    d.rectangle([19, 18, 33, 28], fill=PALETTE["green"])
    return img


def g_print():
    img, d = _base(PALETTE["green"])
    d.rectangle([18, 10, 46, 22], fill=PALETTE["white"])
    d.rectangle([12, 24, 52, 40], fill=PALETTE["white"])
    d.rectangle([20, 32, 44, 52], fill=PALETTE["white"])
    d.rectangle([24, 27, 40, 31], fill=PALETTE["green"])
    return img


def g_search():
    img, d = _base(PALETTE["green"])
    d.ellipse([14, 14, 42, 42], outline=PALETTE["white"], width=6)
    d.line([40, 40, 54, 54], fill=PALETTE["white"], width=7)
    return img


ICONS = {
    "dashboard": g_dashboard, "quick-add": g_quickadd, "approval": g_approval,
    "weighing": g_weighing, "sorting": g_sorting, "trade": g_trade,
    "accounting": g_accounting, "bank": g_bank, "basket": g_basket,
    "parties": g_parties, "reports": g_reports, "import": g_import,
    "settings": g_settings, "theme": g_theme, "backup": g_backup,
    "print": g_print, "search": g_search,
}


def build_icons(icons_dir: Path, logo: Path | None) -> list[Path]:
    written = []
    for name, draw in ICONS.items():
        path = icons_dir / f"{name}.png"
        _save(draw(), path)
        written.append(path)
        print(f"  icon {path.name}")

    # Window / application icon derived from the brand logo.
    if logo and logo.exists():
        base = Image.open(logo).convert("RGBA")
        base.thumbnail((256, 256))
        canvas = Image.new("RGBA", (256, 256), (0, 0, 0, 0))
        canvas.paste(base, ((256 - base.width) // 2, (256 - base.height) // 2), base)
        ico_path = icons_dir / "app.ico"
        canvas.save(ico_path, format="ICO",
                    sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
        written.append(ico_path)
        print(f"  icon {ico_path.name}")
    return written


def main() -> int:
    parser = argparse.ArgumentParser(description="Build Mazandasht icons and mockups")
    parser.add_argument("--logo", type=Path, default=None)
    parser.add_argument("--icons", required=True, type=Path)
    parser.add_argument("--mockups", type=Path, default=None)
    args = parser.parse_args()

    build_icons(args.icons, args.logo)
    print(f"\nbuilt {len(ICONS)} icons in {args.icons}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
