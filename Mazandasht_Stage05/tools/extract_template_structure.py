#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
G1 - Extract the canonical structure of every Mazandasht Excel import template.

Reads each workbook in app/Mazandasht.Desktop/Templates and emits a
machine-readable JSON contract (sheets, data headers, data dictionary, guide
text) so the import engine, the documentation and the tests all share one
source of truth.

Usage:
    python3 tools/extract_template_structure.py \
        --templates app/Mazandasht.Desktop/Templates \
        --out docs/11_Import_Profiles_Canonical.json
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path

from openpyxl import load_workbook

DATA_SHEET = "داده‌ها"
DICT_SHEET = "فرهنگ داده"
GUIDE_SHEET = "راهنما"


def _clean(value):
    if value is None:
        return None
    text = str(value).strip()
    return text if text else None


def extract_profile(path: Path) -> dict:
    wb = load_workbook(path, data_only=False, read_only=True)
    try:
        sheets = list(wb.sheetnames)
        for required in (GUIDE_SHEET, DATA_SHEET, DICT_SHEET):
            if required not in sheets:
                raise SystemExit(f"{path.name}: missing required sheet {required!r}")

        ws = wb[DATA_SHEET]
        headers = []
        for col in range(1, (ws.max_column or 1) + 1):
            headers.append(_clean(ws.cell(1, col).value))
        if not headers or any(h is None for h in headers):
            raise SystemExit(f"{path.name}: blank or missing data header")

        dictionary = []
        d = wb[DICT_SHEET]
        for row in range(2, (d.max_row or 1) + 1):
            title = _clean(d.cell(row, 1).value)
            if not title:
                continue
            dictionary.append({
                "field": title,
                "required": (_clean(d.cell(row, 2).value) or "") == "بله",
                "format": _clean(d.cell(row, 3).value),
                "rule": _clean(d.cell(row, 4).value),
            })

        guide_lines = []
        g = wb[GUIDE_SHEET]
        for row in range(1, (g.max_row or 1) + 1):
            values = [_clean(g.cell(row, col).value) for col in range(1, (g.max_column or 1) + 1)]
            line = " | ".join(v for v in values if v)
            if line:
                guide_lines.append(line)

        data_row_count = 0
        for row in ws.iter_rows(min_row=2, values_only=True):
            if any(v is not None and str(v).strip() for v in row):
                data_row_count += 1

        return {
            "file": path.name,
            "sheets": sheets,
            "headers": headers,
            "dictionary": dictionary,
            "guide": guide_lines,
            "data_rows_present": data_row_count,
            "header_matches_dictionary": [f["field"] for f in dictionary] == headers,
        }
    finally:
        wb.close()


def main() -> int:
    parser = argparse.ArgumentParser(description="Extract Mazandasht Excel template contracts")
    parser.add_argument("--templates", required=True, type=Path)
    parser.add_argument("--out", required=True, type=Path)
    args = parser.parse_args()

    files = sorted(args.templates.glob("*.xlsx"))
    if not files:
        raise SystemExit(f"no .xlsx templates found in {args.templates}")

    profiles = [extract_profile(p) for p in files]
    payload = {
        "schema": "mazandasht.import-profiles/v1",
        "source": "app/Mazandasht.Desktop/Templates",
        "profile_count": len(profiles),
        "profiles": profiles,
    }
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")

    for p in profiles:
        flag = "OK" if p["header_matches_dictionary"] and p["data_rows_present"] == 0 else "CHECK"
        print(f"{flag} {p['file']}: {len(p['headers'])} fields, {len(p['dictionary'])} dictionary rows, "
              f"{p['data_rows_present']} data rows")
    print(f"\nwrote {args.out} ({len(profiles)} profiles)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
