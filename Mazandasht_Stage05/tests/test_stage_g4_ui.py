# -*- coding: utf-8 -*-
"""G4 acceptance tests: theme system, icons, fonts, mockups and asset wiring."""
from __future__ import annotations

import re
import xml.etree.ElementTree as ET
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
APP = ROOT / "app" / "Mazandasht.Desktop"
THEMES = APP / "Assets" / "Themes"
ICONS = APP / "Assets" / "Icons"
FONTS = APP / "Assets" / "Fonts"
VISUALS = ROOT / "docs" / "visuals"
XAML_NS = "{http://schemas.microsoft.com/winfx/2006/xaml}"

REQUIRED_TOKENS = [
    "BrandGreen", "BrandOrange", "BrandLime", "Accent", "WindowBackground",
    "SidebarBackground", "HeaderBackground", "PanelBackground", "CardBackground",
    "GlassBackground", "GlassBorder", "SelectedBackground", "Text", "MutedText",
    "Danger", "Success", "Warning",
]


def _keys(path: Path) -> set[str]:
    tree = ET.parse(path)
    keys = set()
    for element in tree.getroot().iter():
        key = element.attrib.get(f"{XAML_NS}Key") or element.attrib.get("Key")
        if key:
            keys.add(key)
    return keys


def test_all_xaml_files_parse():
    files = list((APP).rglob("*.xaml"))
    assert len(files) >= 8
    for path in files:
        ET.parse(path)  # raises on malformed XAML


def test_theme_dictionaries_expose_the_same_token_set():
    theme_files = sorted(THEMES.glob("Theme.*.xaml"))
    assert [p.name for p in theme_files] == [
        "Theme.Caspian.xaml", "Theme.Dark.xaml", "Theme.Forest.xaml", "Theme.Windows.xaml"]
    sets = {}
    for path in theme_files:
        keys = _keys(path)
        for token in REQUIRED_TOKENS:
            assert token in keys, f"{path.name}: missing color {token}"
            assert token + "Brush" in keys, f"{path.name}: missing brush {token}Brush"
        sets[path.name] = keys
    reference = sets["Theme.Windows.xaml"]
    for name, keys in sets.items():
        assert keys == reference, f"{name} token set differs from Windows"


def test_common_styles_reference_existing_theme_resources():
    common = THEMES / "Common.xaml"
    text = common.read_text(encoding="utf-8")
    referenced = set(re.findall(r"\{DynamicResource\s+([A-Za-z0-9_]+)\}", text))
    theme_keys = _keys(THEMES / "Theme.Windows.xaml")
    missing = sorted(key for key in referenced if key not in theme_keys)
    assert not missing, f"Common.xaml references unknown resources: {missing}"


def test_icon_set_is_complete_and_sized():
    expected = {"dashboard", "quick-add", "approval", "weighing", "sorting", "trade",
                "accounting", "bank", "basket", "parties", "reports", "import",
                "settings", "theme", "backup", "print", "search"}
    present = {p.stem for p in ICONS.glob("*.png")}
    assert expected <= present, f"missing icons: {expected - present}"
    for name in expected:
        with Image.open(ICONS / f"{name}.png") as img:
            assert img.size == (64, 64), name
            assert img.mode == "RGBA", name
    with Image.open(ICONS / "app.ico") as ico:
        assert ico.format == "ICO"
        assert ico.size == (256, 256)


def test_brand_font_is_bundled():
    assert (FONTS / "Vazirmatn-Regular.ttf").stat().st_size > 50000
    assert (FONTS / "Vazirmatn-Bold.ttf").stat().st_size > 50000


def test_mockups_are_valid_svg():
    for name in ("ui-mockup-light.svg", "ui-mockup-dark.svg"):
        path = VISUALS / name
        assert path.exists(), name
        root = ET.parse(path).getroot()
        assert root.tag.endswith("svg")
        assert root.attrib["width"] == "1600"


def test_design_system_mockups_are_valid_svg():
    for name in ("ui-design-system-light.svg", "ui-design-system-dark.svg"):
        path = VISUALS / name
        assert path.exists(), name
        root = ET.parse(path).getroot()
        assert root.tag.endswith("svg")
        assert root.attrib["width"] == "1200"
        text = path.read_text(encoding="utf-8")
        assert "کتابخانه اجزای رابط کاربری مازندشت" in text


def test_project_file_wires_new_assets():
    csproj = (APP / "Mazandasht.Desktop.csproj").read_text(encoding="utf-8")
    assert "Assets\\Icons\\*.png" in csproj
    assert "Assets\\Fonts\\*.ttf" in csproj
    assert "app.ico" in csproj
    # The WindowsDesktop SDK already glob-includes **/*.xaml as Page items;
    # listing the theme dictionaries again would be a duplicate-item build error.
    assert "Assets\\Themes\\*.xaml" not in csproj


def test_navigation_and_header_reference_existing_icon_assets():
    main_window = (APP / "MainWindow.xaml").read_text(encoding="utf-8")
    referenced = set(re.findall(r'Source="(Assets/Icons/[^"]+\.png)"', main_window))
    assert len(referenced) >= 15, f"navigation icons not wired: {sorted(referenced)}"
    for relative in referenced:
        assert (APP / relative).exists(), relative
    for required in ("Assets/Icons/dashboard.png", "Assets/Icons/weighing.png",
                     "Assets/Icons/accounting.png", "Assets/Icons/bank.png",
                     "Assets/Icons/basket.png", "Assets/Icons/parties.png",
                     "Assets/Icons/import.png", "Assets/Icons/settings.png",
                     "Assets/Icons/theme.png", "Assets/Icons/backup.png"):
        assert required in referenced, required


def test_theme_service_references_existing_theme_files():
    source = (APP / "Services" / "ThemeService.cs").read_text(encoding="utf-8")
    referenced = re.findall(r'"(Assets/Themes/Theme\.[A-Za-z]+\.xaml)"', source)
    assert set(referenced) == {
        "Assets/Themes/Theme.Windows.xaml", "Assets/Themes/Theme.Dark.xaml",
        "Assets/Themes/Theme.Caspian.xaml", "Assets/Themes/Theme.Forest.xaml"}
    for relative in referenced:
        assert (APP / relative).exists(), relative


def test_default_theme_is_windows_light():
    app_xaml = (APP / "App.xaml").read_text(encoding="utf-8")
    assert "Assets/Themes/Theme.Windows.xaml" in app_xaml
    theme = (THEMES / "Theme.Windows.xaml").read_text(encoding="utf-8")
    assert "#F3F3F3" in theme  # light window background


def _schema_v6_connection():
    import sqlite3
    connection = sqlite3.connect(":memory:")
    connection.execute("PRAGMA foreign_keys=ON")
    for name in ["schema_v1.sql", "system_seed.sql", "migration_002.sql", "migration_003.sql",
                 "migration_004.sql", "migration_005.sql", "migration_006.sql", "migration_007.sql"]:
        connection.executescript((APP / "Database" / name).read_text(encoding="utf-8"))
    return connection


def test_theme_setting_persists_and_roundtrips():
    """The exact upsert used by ThemeService.SaveTheme must round-trip."""
    source = (APP / "Services" / "ThemeService.cs").read_text(encoding="utf-8")
    match = re.search(r'const string sql = @"(.*?)";', source, re.S)
    assert match, "ThemeService upsert statement not found"
    sql = match.group(1)
    connection = _schema_v6_connection()
    try:
        for theme, user in (("Dark", "user-1"), ("Caspian", "user-2")):
            connection.execute(sql, {"key": "ui.theme", "value": theme,
                                      "category": "UI", "user": user})
        connection.commit()
        rows = connection.execute(
            "SELECT value_json, updated_by FROM app_settings WHERE setting_key='ui.theme'").fetchall()
        assert rows == [("Caspian", "user-2")]
    finally:
        connection.close()


def test_saved_theme_is_applied_before_login_window():
    app_cs = (APP / "App.xaml.cs").read_text(encoding="utf-8")
    assert "ThemeService.ApplySavedTheme(_databasePath)" in app_cs
    main_window = (APP / "MainWindow.xaml").read_text(encoding="utf-8")
    assert 'x:Name="ThemeButton"' in main_window
    main_cs = (APP / "MainWindow.xaml.cs").read_text(encoding="utf-8")
    assert "ThemePreviewWindow" in main_cs
