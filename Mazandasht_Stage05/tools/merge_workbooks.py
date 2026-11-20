#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
G1 - Merge and correct Mazandasht import workbooks of the same profile.

Concatenates the data rows of several same-profile workbooks into one workbook
that keeps the profile's exact header contract, flags duplicate unique-key rows
instead of silently dropping them, and can update existing rows in correction
mode (same unique key -> replace the older row).

No row is ever discarded without being reported. The merged workbook is written
to a caller-chosen path; the blank templates under Templates/ are never touched.

Usage:
    python3 tools/merge_workbooks.py --contract docs/11_Import_Profiles_Canonical.json \
        --profile 01_Parties.xlsx --out /tmp/merged.xlsx file1.xlsx file2.xlsx ...
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

from openpyxl import Workbook, load_workbook
from openpyxl.styles import Alignment, Font, PatternFill

DATA_SHEET = "داده‌ها"
HEADER_FILL = "1B5E3A"
DUP_FILL = "F6DAD7"
UPDATED_FILL = "FFF3CD"


def _field_kind(field: str, fmt: str | None) -> str:
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
    return "text"


def _read_rows(path: Path, headers: list[str]) -> tuple[list[dict], list[str]]:
    """Return (rows, problems). Rows are dicts keyed by the canonical header."""
    problems: list[str] = []
    wb = load_workbook(path, data_only=True, read_only=True)
    try:
        if DATA_SHEET not in wb.sheetnames:
            return [], [f"{path.name}: sheet {DATA_SHEET!r} missing"]
        ws = wb[DATA_SHEET]
        local_headers = [ws.cell(1, c).value for c in range(1, len(headers) + 1)]
        if [str(h).strip() if h else None for h in local_headers] != headers:
            return [], [f"{path.name}: header row does not match the profile contract"]
        rows: list[dict] = []
        for row_idx, values in enumerate(ws.iter_rows(min_row=2, values_only=True), start=2):
            if all(v is None or str(v).strip() == "" for v in values):
                continue
            record = {}
            for idx, header in enumerate(headers):
                value = values[idx] if idx < len(values) else None
                record[header] = value
            record["__source__"] = path.name
            record["__row__"] = row_idx
            rows.append(record)
        return rows, problems
    finally:
        wb.close()


def _unique_key(fields: list[dict]) -> str | None:
    for f in fields:
        if _field_kind(f["field"], f.get("format")) == "unique_text":
            return f["field"]
    return None


def main() -> int:
    parser = argparse.ArgumentParser(description="Merge Mazandasht import workbooks")
    parser.add_argument("--contract", required=True, type=Path)
    parser.add_argument("--profile", required=True)
    parser.add_argument("--out", required=True, type=Path)
    parser.add_argument("files", nargs="+", type=Path)
    args = parser.parse_args()

    contract = json.loads(args.contract.read_text(encoding="utf-8"))
    profile = next((p for p in contract["profiles"] if p["file"] == args.profile), None)
    if profile is None:
        print(f"error: profile {args.profile!r} not found in contract", file=sys.stderr)
        return 2

    fields = profile["dictionary"]
    headers = [f["field"] for f in fields]
    key = _unique_key(fields)

    all_rows: list[dict] = []
    problems: list[str] = []
    for path in args.files:
        rows, issues = _read_rows(path, headers)
        problems.extend(issues)
        all_rows.extend(rows)

    seen: dict[str, dict] = {}
    duplicates: list[dict] = []
    updated: list[dict] = []
    for row in all_rows:
        if not key:
            continue
        value = row.get(key)
        if value is None or str(value).strip() == "":
            problems.append(f"{row['__source__']} row {row['__row__']}: empty unique key {key!r}")
            continue
        token = str(value).strip()
        if token in seen:
            duplicates.append(row)
            # Correction mode: later file wins, the earlier row is replaced.
            target_index = next(i for i, r in enumerate(seen[token]["__merged__"])
                                if r is seen[token]["__row__"])
            seen[token]["__merged__"][target_index] = row
            seen[token]["__row__"] = row
            updated.append(row)
        else:
            entry = {"__row__": row, "__merged__": [row]}
            seen[token] = entry

    merged_rows: list[dict] = []
    for token, entry in seen.items():
        merged_rows.extend(entry["__merged__"])

    wb = Workbook()
    ws = wb.active
    ws.title = DATA_SHEET
    for idx, header in enumerate(headers, start=1):
        c = ws.cell(1, idx, header)
        c.font = Font(bold=True, color="FFFFFF")
        c.fill = PatternFill("solid", fgColor=HEADER_FILL)
        c.alignment = Alignment(horizontal="center", vertical="center")
    for r, row in enumerate(merged_rows, start=2):
        for idx, header in enumerate(headers, start=1):
            ws.cell(r, idx, row.get(header))

    dup_keys = {str(r.get(key)).strip() for r in duplicates if key}
    for r, row in enumerate(merged_rows, start=2):
        if key and str(row.get(key)).strip() in dup_keys:
            for idx in range(1, len(headers) + 1):
                ws.cell(r, idx).fill = PatternFill("solid", fgColor=DUP_FILL)

    args.out.parent.mkdir(parents=True, exist_ok=True)
    wb.save(args.out)

    print(f"profile        : {args.profile}")
    print(f"unique key     : {key}")
    print(f"files merged   : {len(args.files)}")
    print(f"rows read      : {len(all_rows)}")
    print(f"rows written   : {len(merged_rows)}")
    print(f"duplicates     : {len(duplicates)} (corrected in place, highlighted)")
    print(f"updates applied: {len(updated)}")
    print(f"problems       : {len(problems)}")
    for problem in problems:
        print(f"  - {problem}")
    print(f"output         : {args.out}")
    return 0 if not problems else 1


if __name__ == "__main__":
    raise SystemExit(main())
