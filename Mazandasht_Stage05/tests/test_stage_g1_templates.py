# -*- coding: utf-8 -*-
"""G1 acceptance tests: Excel template contract, themes, validation, merge."""
from __future__ import annotations

import json
import subprocess
import sys
from pathlib import Path

from openpyxl import load_workbook

ROOT = Path(__file__).resolve().parents[1]
APP = ROOT / "app" / "Mazandasht.Desktop"
TEMPLATES = APP / "Templates"
TOOLS = ROOT / "tools"
CONTRACT = ROOT / "docs" / "11_Import_Profiles_Canonical.json"

DATA_SHEET = "داده‌ها"
DICT_SHEET = "فرهنگ داده"
GUIDE_SHEET = "راهنما"
THEMES = ["Light", "Dark", "Caspian"]


def _theme_dir(theme: str) -> Path:
    return TEMPLATES if theme == "Light" else TEMPLATES / "Themes" / theme


def _headers(ws):
    return [ws.cell(1, col).value for col in range(1, ws.max_column + 1)]


def _dictionary_fields(ws):
    return [ws.cell(row, 1).value for row in range(2, ws.max_row + 1) if ws.cell(row, 1).value]


def test_contract_has_eleven_profiles_and_matches_templates():
    contract = json.loads(CONTRACT.read_text(encoding="utf-8"))
    profiles = contract["profiles"]
    assert contract["schema"] == "mazandasht.import-profiles/v1"
    assert len(profiles) == 11
    assert sorted(p["file"] for p in profiles) == sorted(p.name for p in TEMPLATES.glob("*.xlsx"))
    for profile in profiles:
        assert profile["header_matches_dictionary"], profile["file"]
        assert profile["data_rows_present"] == 0, profile["file"]
        assert len(profile["dictionary"]) == len(profile["headers"])


def test_all_three_themes_are_published():
    for theme in THEMES:
        files = sorted(_theme_dir(theme).glob("*.xlsx"))
        assert len(files) == 11, f"{theme}: expected 11 workbooks, found {len(files)}"


def test_every_workbook_keeps_the_owner_contract():
    for theme in THEMES:
        for path in sorted(_theme_dir(theme).glob("*.xlsx")):
            wb = load_workbook(path)
            try:
                assert wb.sheetnames == [GUIDE_SHEET, DATA_SHEET, DICT_SHEET], path
                ws = wb[DATA_SHEET]
                assert _headers(ws) == _dictionary_fields(wb[DICT_SHEET]), path
                for row in ws.iter_rows(min_row=2, values_only=True):
                    assert all(value is None for value in row), f"template has data: {path}"
                assert ws.freeze_panes == "A2", path
                assert ws.auto_filter.ref is not None, path
            finally:
                wb.close()


def test_data_sheets_carry_real_validation_and_formatting():
    contract = json.loads(CONTRACT.read_text(encoding="utf-8"))
    for profile in contract["profiles"]:
        kinds = {}
        for field in profile["dictionary"]:
            text = (field["field"] or "") + " " + (field.get("format") or "")
            if "IN / OUT" in text or "IN/OUT" in text:
                kinds[field["field"]] = "direction"
            if "متن یکتا" in (field.get("format") or ""):
                kinds[field["field"]] = "unique"

        wb = load_workbook(TEMPLATES / profile["file"])
        try:
            ws = wb[DATA_SHEET]
            header_index = {h: i + 1 for i, h in enumerate(_headers(ws))}
            validations = [dv for dv in ws.data_validations.dataValidation]
            for field, kind in kinds.items():
                if kind == "direction":
                    assert validations, f"{profile['file']}: no data validation at all"
                    assert any("IN" in (dv.formula1 or "") for dv in validations), \
                        f"{profile['file']}: direction drop-down missing for {field}"
                if kind == "unique":
                    ranges = [str(rng.sqref) for rng in ws.conditional_formatting]
                    letter = chr(ord("A") + header_index[field] - 1)
                    assert any(str(rng).startswith(letter) for rng in ranges), \
                        f"{profile['file']}: duplicate highlighting missing for {field}"
        finally:
            wb.close()


def test_extractor_round_trips(tmp_path):
    out = tmp_path / "contract.json"
    subprocess.run(
        [sys.executable, str(TOOLS / "extract_template_structure.py"),
         "--templates", str(TEMPLATES), "--out", str(out)],
        check=True, capture_output=True, text=True)
    regenerated = json.loads(out.read_text(encoding="utf-8"))
    current = json.loads(CONTRACT.read_text(encoding="utf-8"))
    assert regenerated["profiles"] == current["profiles"]


def test_merge_tool_deduplicates_and_reports(tmp_path):
    contract = json.loads(CONTRACT.read_text(encoding="utf-8"))
    profile = next(p for p in contract["profiles"] if p["file"] == "01_Parties.xlsx")
    headers = [f["field"] for f in profile["dictionary"]]

    def make(path: Path, rows):
        wb = load_workbook(TEMPLATES / "01_Parties.xlsx")
        ws = wb[DATA_SHEET]
        for r, row in enumerate(rows, start=2):
            for c, value in enumerate(row, start=1):
                ws.cell(r, c, value)
        wb.save(path)
        wb.close()

    make(tmp_path / "a.xlsx", [["P-001", "شرکت نمونه یک"], ["P-002", "شرکت نمونه دو"]])
    make(tmp_path / "b.xlsx", [["P-002", "شرکت نمونه دو - اصلاح‌شده"], ["P-003", "شرکت نمونه سه"]])

    out = tmp_path / "merged.xlsx"
    result = subprocess.run(
        [sys.executable, str(TOOLS / "merge_workbooks.py"),
         "--contract", str(CONTRACT), "--profile", "01_Parties.xlsx",
         "--out", str(out), str(tmp_path / "a.xlsx"), str(tmp_path / "b.xlsx")],
        capture_output=True, text=True)
    assert result.returncode == 0, result.stdout + result.stderr
    assert "duplicates     : 1" in result.stdout
    assert "rows written   : 3" in result.stdout

    wb = load_workbook(out)
    try:
        ws = wb[DATA_SHEET]
        names = [ws.cell(r, 2).value for r in range(2, ws.max_row + 1)]
        assert names == ["شرکت نمونه یک", "شرکت نمونه دو - اصلاح‌شده", "شرکت نمونه سه"]
        assert _headers(ws) == headers
    finally:
        wb.close()
