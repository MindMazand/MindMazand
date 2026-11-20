# -*- coding: utf-8 -*-
"""Stage G12 — Windows x64 build, installer and compatibility.

The build itself cannot run in this Linux sandbox (BLK-001), so these tests
verify the authored pipeline: the project/solution target, the build script, the
output-verification script (executed for real against a synthetic output tree)
and the Inno Setup installer script. Nothing here claims that a Windows build or
an installer compilation succeeded.
"""
import pathlib
import re
import subprocess
import sys
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]
APP = ROOT / "app" / "Mazandasht.Desktop"
INSTALLER = ROOT / "installer"


class StageG12BuildInstaller(unittest.TestCase):
    def test_project_targets_x64(self):
        csproj = (APP / "Mazandasht.Desktop.csproj").read_text(encoding="utf-8")
        self.assertIn("<PlatformTarget>x64</PlatformTarget>", csproj)
        self.assertIn("<Prefer32Bit>false</Prefer32Bit>", csproj)
        self.assertIn("<TargetFramework>net48</TargetFramework>", csproj)
        self.assertIn("<LangVersion>7.3</LangVersion>", csproj)

    def test_solution_has_x64_configurations(self):
        sln = (ROOT / "app" / "Mazandasht.sln").read_text(encoding="utf-8")
        self.assertIn("Release|x64 = Release|x64", sln)
        self.assertRegex(sln, r"\.Release\|x64\.Build\.0 = Release\|x64")

    def test_app_config_probes_the_sqlite_interop_folders(self):
        config = (APP / "App.config").read_text(encoding="utf-8")
        self.assertIn('<supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.8"/>', config)
        self.assertIn('<probing privatePath="x86;x64"/>', config)

    def test_application_manifest_declares_the_supported_windows_versions(self):
        manifest = (APP / "app.manifest").read_text(encoding="utf-8")
        # Windows 8.1 and Windows 10 GUIDs.
        self.assertIn("{1f676c76-80e1-4239-95bb-83d0f6d0da78}", manifest)
        self.assertIn("{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}", manifest)
        self.assertIn('requestedExecutionLevel level="asInvoker"', manifest)

    def test_build_script_builds_x64_release_then_verifies_then_installs(self):
        bat = (ROOT / "build-windows.bat").read_text(encoding="utf-8")
        self.assertIn("/p:Configuration=Release", bat)
        self.assertIn("/p:Platform=x64", bat)
        self.assertIn("verify_windows_build.py", bat)
        self.assertIn("Mazandasht_Setup.iss", bat)
        self.assertIn("Inno Setup 6", bat)
        # A missing MSBuild must fail loudly, not be ignored.
        self.assertIn("ERROR: MSBuild not found", bat)
        self.assertIn("exit /b 2", bat)

    def test_build_script_never_claims_success_without_a_check(self):
        bat = (ROOT / "build-windows.bat").read_text(encoding="utf-8")
        self.assertIn("VERIFICATION FAILED", bat)
        self.assertIn("BUILD FAILED", bat)
        self.assertIn("run Mazandasht.Desktop.exe once on the target Windows version", bat)

    # ------------------------------------------------------------------
    # the verification script, executed for real
    def _run_verify(self, directory):
        return subprocess.run(
            [sys.executable, str(ROOT / "tools" / "verify_windows_build.py"), str(directory)],
            capture_output=True, text=True)

    def _make_output(self, root, *, interop=True, sql=8, templates=11, fonts=2, icons=17):
        (root / "x64").mkdir(parents=True, exist_ok=True)
        (root / "x86").mkdir(parents=True, exist_ok=True)
        (root / "Database").mkdir(parents=True, exist_ok=True)
        (root / "Templates" / "Themes").mkdir(parents=True, exist_ok=True)
        (root / "Assets" / "Fonts").mkdir(parents=True, exist_ok=True)
        (root / "Assets" / "Icons").mkdir(parents=True, exist_ok=True)
        (root / "Mazandasht.Desktop.exe").write_text("exe", encoding="utf-8")
        (root / "Mazandasht.Desktop.exe.config").write_text("config", encoding="utf-8")
        (root / "mazandasht-logo.png").write_text("logo", encoding="utf-8")
        if interop:
            (root / "x64" / "SQLite.Interop.dll").write_text("interop", encoding="utf-8")
        for index in range(sql):
            (root / "Database" / ("%02d.sql" % index)).write_text("-- sql", encoding="utf-8")
        for index in range(templates):
            (root / "Templates" / ("T%02d.xlsx" % index)).write_text("xlsx", encoding="utf-8")
        for index in range(fonts):
            (root / "Assets" / "Fonts" / ("F%02d.ttf" % index)).write_text("ttf", encoding="utf-8")
        for index in range(icons):
            (root / "Assets" / "Icons" / ("I%02d.png" % index)).write_text("png", encoding="utf-8")

    def test_verify_script_accepts_a_complete_output_tree(self):
        with tempfile.TemporaryDirectory() as directory:
            self._make_output(pathlib.Path(directory))
            result = self._run_verify(directory)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            self.assertIn("OK: the x64 Release output looks complete.", result.stdout)
            # A file check must not be reported as an executed application.
            self.assertIn("file check only", result.stdout)

    def test_verify_script_rejects_a_missing_sqlite_interop(self):
        with tempfile.TemporaryDirectory() as directory:
            self._make_output(pathlib.Path(directory), interop=False)
            result = self._run_verify(directory)
            self.assertEqual(result.returncode, 1)
            self.assertIn("missing x64/SQLite.Interop.dll", result.stdout)

    def test_verify_script_rejects_a_missing_sql_script(self):
        with tempfile.TemporaryDirectory() as directory:
            self._make_output(pathlib.Path(directory), sql=3)
            result = self._run_verify(directory)
            self.assertEqual(result.returncode, 1)
            self.assertIn("expected at least", result.stdout)

    def test_verify_script_rejects_a_missing_executable(self):
        with tempfile.TemporaryDirectory() as directory:
            self._make_output(pathlib.Path(directory))
            (pathlib.Path(directory) / "Mazandasht.Desktop.exe").unlink()
            result = self._run_verify(directory)
            self.assertEqual(result.returncode, 1)
            self.assertIn("missing Mazandasht.Desktop.exe", result.stdout)

    def test_verify_script_requires_the_blank_templates(self):
        with tempfile.TemporaryDirectory() as directory:
            self._make_output(pathlib.Path(directory), templates=4)
            result = self._run_verify(directory)
            self.assertEqual(result.returncode, 1)
            self.assertIn("Templates/", result.stdout)

    def test_verify_script_requires_the_brand_fonts_and_icons(self):
        with tempfile.TemporaryDirectory() as directory:
            self._make_output(pathlib.Path(directory), fonts=1, icons=3)
            result = self._run_verify(directory)
            self.assertEqual(result.returncode, 1)
            self.assertIn("Assets/Fonts", result.stdout)
            self.assertIn("Assets/Icons", result.stdout)

    # ------------------------------------------------------------------
    # installer
    def test_installer_is_inno_setup_6_and_per_user(self):
        iss = (INSTALLER / "Mazandasht_Setup.iss").read_text(encoding="utf-8")
        self.assertIn("#define MyAppVersion", iss)
        self.assertIn("PrivilegesRequired=lowest", iss)
        self.assertIn("DefaultDirName={localappdata}\\Mazandasht", iss)
        self.assertIn("ArchitecturesAllowed=x64", iss)
        self.assertIn("ArchitecturesInstallIn64BitMode=x64", iss)
        self.assertIn("MinVersion=6.3.9600", iss)

    def test_installer_copies_the_files_the_app_needs_at_runtime(self):
        iss = (INSTALLER / "Mazandasht_Setup.iss").read_text(encoding="utf-8")
        for fragment in ("{#MyAppExeName}", "SQLite.Interop.dll", "Database\\*.sql",
                         "Templates\\*.xlsx", "Assets\\Fonts\\*", "Assets\\Icons\\*",
                         "Assets\\Themes\\*", "mazandasht-logo.png"):
            self.assertIn(fragment, iss)
        # Both interop flavours are shipped even though only x64 is installed.
        self.assertIn('DestDir: "{app}\\x64"', iss)
        self.assertIn('DestDir: "{app}\\x86"', iss)

    def test_installer_creates_shortcuts_and_data_folders(self):
        iss = (INSTALLER / "Mazandasht_Setup.iss").read_text(encoding="utf-8")
        self.assertIn("[Icons]", iss)
        self.assertIn("{autodesktop}", iss)
        self.assertIn("{group}", iss)
        for folder in ("Data", "Backups", "Exports"):
            self.assertIn("{localappdata}\\Mazandasht\\" + folder, iss)

    def test_installer_never_touches_the_database_on_uninstall(self):
        iss = (INSTALLER / "Mazandasht_Setup.iss").read_text(encoding="utf-8")
        self.assertNotIn("[UninstallDelete]", iss)
        self.assertIn("outside the install", iss)

    def test_installer_readme_documents_the_prerequisites(self):
        readme = (INSTALLER / "README_FA.md").read_text(encoding="utf-8")
        for token in ("Visual Studio 2022", ".NET Framework 4.8", "Inno Setup 6",
                      "build-windows.bat", "x64"):
            self.assertIn(token, readme)

    # ------------------------------------------------------------------
    def test_package_manifest_declares_the_build_and_installer(self):
        import json
        manifest = json.loads((ROOT / "PACKAGE_MANIFEST.json").read_text(encoding="utf-8"))
        self.assertEqual(manifest["version"], "0.5.2-alpha3")
        self.assertEqual(manifest["build"]["platform"], "x64")
        self.assertEqual(manifest["build"]["configuration"], "Release")
        self.assertIn("not_run", manifest["windows_build"])
        self.assertIn("not_compiled", manifest["installer"])

    def test_g12_report_does_not_claim_a_successful_windows_build(self):
        report = (ROOT / "docs" / "G12_Build_Installer_Windows_Report_FA.md").read_text(encoding="utf-8")
        self.assertIn("BLK-001", report)
        self.assertNotIn("BUILD SUCCEEDED", report)
        self.assertRegex(report, r"(اجرای واقعی|not run|blocked)")

    # ------------------------------------------------------------------
    # prerequisites installer and Windows script hygiene
    def test_prerequisites_installer_exists_and_only_adds_missing_tools(self):
        bat = (ROOT / "setup-windows-prerequisites.bat").read_text(encoding="utf-8")
        for token in ("Microsoft.VisualStudio.2022.BuildTools",
                      "Microsoft.Net.Component.4.8.TargetingPack",
                      "JRSoftware.InnoSetup",
                      "Python.Python.3.11",
                      "winget install", "--yes"):
            self.assertIn(token, bat)
        # It must never uninstall anything.
        self.assertNotIn("winget uninstall", bat)
        self.assertNotIn("winget remove", bat)

    def test_prerequisites_installer_handles_a_machine_without_winget(self):
        bat = (ROOT / "setup-windows-prerequisites.bat").read_text(encoding="utf-8")
        self.assertIn(":no_winget", bat)
        self.assertIn("installer\\README_FA.md", bat)
        self.assertRegex(bat, r"exit /b 3")

    def test_build_script_finds_msbuild_through_vswhere(self):
        """MSBuild is not on PATH in a normal command window."""
        bat = (ROOT / "build-windows.bat").read_text(encoding="utf-8")
        self.assertIn("vswhere.exe", bat)
        self.assertIn("Microsoft.Component.MSBuild", bat)
        self.assertIn("setup-windows-prerequisites.bat", bat)
        # A missing MSBuild still fails loudly.
        self.assertIn("ERROR: MSBuild not found", bat)
        self.assertIn("exit /b 2", bat)

    def test_build_script_keeps_the_window_open_for_double_click(self):
        bat = (ROOT / "build-windows.bat").read_text(encoding="utf-8")
        self.assertIn("pause", bat)

    def test_windows_scripts_use_crlf_line_endings(self):
        """cmd.exe mis-parses LF-only .bat files; Inno Setup expects CRLF too."""
        for name in ("build-windows.bat", "setup-windows-prerequisites.bat",
                     "installer/Mazandasht_Setup.iss"):
            data = (ROOT / name).read_bytes()
            self.assertGreater(data.count(b"\r\n"), 0, name)
            self.assertEqual(data.count(b"\n") - data.count(b"\r\n"), 0,
                             name + " has bare LF line endings")

    def test_installer_script_is_utf8_with_bom(self):
        """The .iss carries Persian strings; without a BOM Inno Setup reads it
        as ANSI and the installer UI turns into mojibake."""
        data = (ROOT / "installer" / "Mazandasht_Setup.iss").read_bytes()
        self.assertTrue(data.startswith(b"\xef\xbb\xbf"))

    def test_readme_documents_the_prerequisites_installer(self):
        readme = (INSTALLER / "README_FA.md").read_text(encoding="utf-8")
        self.assertIn("setup-windows-prerequisites.bat", readme)
        self.assertIn("--yes", readme)


if __name__ == "__main__":
    unittest.main()
