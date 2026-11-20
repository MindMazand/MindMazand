#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Verify a Windows x64 Release build of Mazandasht before it is shipped.

Run on Windows after ``build-windows.bat``::

    python tools\\verify_windows_build.py app\\Mazandasht.Desktop\\bin\\x64\\Release

The script only inspects files; it never claims that the application was
executed. It fails (exit code 1) when a file the application needs at runtime is
missing, because a missing SQLite interop DLL or SQL script is the most common
reason a WPF build "succeeds" and then dies on first launch.
"""
import pathlib
import sys

REQUIRED = [
    "Mazandasht.Desktop.exe",
    "Mazandasht.Desktop.exe.config",
    "mazandasht-logo.png",
]

RECURSING_DIRS = ["Database", "Templates", "Assets"]

MIN_SQL_SCRIPTS = 8          # schema_v1 + system_seed + migration_002..007
MIN_TEMPLATES = 11           # the canonical blank workbooks
MIN_FONTS = 2                # Vazirmatn Regular + Bold


def fail(message):
    print("FAIL: " + message)
    return 1


def main(output_dir):
    root = pathlib.Path(output_dir).resolve()
    if not root.is_dir():
        return fail("output directory not found: " + str(root))

    problems = 0
    for name in REQUIRED:
        if not (root / name).is_file():
            problems += fail("missing " + name)

    # The native SQLite interop library must sit beside the exe (x64).
    interop = root / "x64" / "SQLite.Interop.dll"
    if not interop.is_file():
        problems += fail("missing x64/SQLite.Interop.dll (System.Data.SQLite cannot start)")
    x86_interop = root / "x86" / "SQLite.Interop.dll"
    if not x86_interop.is_file():
        print("WARN: x86/SQLite.Interop.dll is absent (x64-only build, that is expected)")

    database = root / "Database"
    if not database.is_dir():
        problems += fail("missing Database/ content folder")
    else:
        sql = list(database.glob("*.sql"))
        if len(sql) < MIN_SQL_SCRIPTS:
            problems += fail("Database/ has %d .sql files, expected at least %d"
                             % (len(sql), MIN_SQL_SCRIPTS))
        else:
            print("ok: %d SQL scripts" % len(sql))

    templates = root / "Templates"
    if not templates.is_dir():
        problems += fail("missing Templates/ content folder")
    else:
        workbooks = list(templates.glob("*.xlsx"))
        if len(workbooks) < MIN_TEMPLATES:
            problems += fail("Templates/ has %d .xlsx files, expected at least %d"
                             % (len(workbooks), MIN_TEMPLATES))
        else:
            print("ok: %d blank workbooks" % len(workbooks))
        themes = list((templates / "Themes").glob("*.xlsx")) if (templates / "Themes").is_dir() else []
        print("ok: %d themed workbook variants" % len(themes))

    fonts = list((root / "Assets" / "Fonts").glob("*.ttf")) if (root / "Assets" / "Fonts").is_dir() else []
    if len(fonts) < MIN_FONTS:
        problems += fail("Assets/Fonts has %d .ttf files, expected at least %d"
                         % (len(fonts), MIN_FONTS))
    else:
        print("ok: %d brand fonts" % len(fonts))

    icons = list((root / "Assets" / "Icons").glob("*.png")) if (root / "Assets" / "Icons").is_dir() else []
    if len(icons) < 17:
        problems += fail("Assets/Icons has %d .png files, expected at least 17" % len(icons))
    else:
        print("ok: %d icons" % len(icons))

    for name in RECURSING_DIRS:
        if not (root / name).is_dir():
            problems += fail("missing %s/ folder" % name)

    if problems:
        print("\n%d problem(s). Do not ship this build." % problems)
        return 1

    print("\nOK: the x64 Release output looks complete.")
    print("NOTE: this is a file check only. Start the application once on the "
          "target Windows version before delivering it.")
    return 0


if __name__ == "__main__":
    if len(sys.argv) != 2:
        print(__doc__)
        sys.exit(2)
    sys.exit(main(sys.argv[1]))
