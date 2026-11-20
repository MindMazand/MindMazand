# -*- coding: utf-8 -*-
"""Stage G10 — management dashboard, analytical reports and the second display.

Acceptance criteria covered:
  * management reports are read-only views over posted data;
  * every analytical report is exportable to CSV (document export already was);
  * the second display is a read-only, self-refreshing window and exposes no
    control that can change data;
  * the dashboard KPI numbers come from DashboardQueryService/ProfitLossService.
"""
import os
import pathlib
import re
import sqlite3
import subprocess
import uuid as _uuid


def uuid4hex():
    return _uuid.uuid4().hex
import sys
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]
APP = ROOT / "app" / "Mazandasht.Desktop"
MIGRATIONS = [APP / "Database" / name for name in (
    "migration_002.sql", "migration_003.sql", "migration_004.sql",
    "migration_005.sql", "migration_006.sql", "migration_007.sql")]


def script():
    return pathlib.Path(sys.executable).with_name("sqlite3") if False else None


def bootstrap():
    connection = sqlite3.connect(":memory:")
    connection.executescript((APP / "Database" / "schema_v1.sql").read_text(encoding="utf-8"))
    connection.executescript((APP / "Database" / "system_seed.sql").read_text(encoding="utf-8"))
    for migration in MIGRATIONS:
        connection.executescript(migration.read_text(encoding="utf-8"))
    return connection


class StageG10DashboardReports(unittest.TestCase):
    def setUp(self):
        self.connection = bootstrap()
        role = "role-" + uuid4hex()
        self.connection.execute(
            "INSERT INTO roles(id,role_code,display_name,is_active) VALUES(?,?,?,1)",
            (role, "REPORT_VIEWER", "مشاهدهٔ گزارش"))
        self.connection.execute(
            "INSERT INTO role_permissions(role_id,permission_code,granted) "
            "VALUES(?,'reports.view',1)", (role,))
        self.connection.execute(
            "INSERT INTO users(id,username,display_name,password_hash,password_salt,"
            "password_iterations,role_id,is_active) "
            "VALUES('u-rep','report.viewer','مراجع گزارش','h','s',100000,?,1)", (role,))
        self.connection.commit()

    def tearDown(self):
        self.connection.close()

    # ------------------------------------------------------------------
    def test_report_service_catalog_and_definitions(self):
        service = (APP / "Services" / "ReportService.cs").read_text(encoding="utf-8")
        self.assertIn("public static IList<ReportDefinition> Available()", service)
        self.assertIn("public static DataTable Build(", service)
        for code in ("STOCK_ON_HAND", "PARTY_BALANCE", "BANK_BALANCE",
                     "CHEQUE_REGISTER", "DAILY_FLOW", "PROFIT_LOSS", "DOCUMENTS"):
                self.assertIn('Code = "' + code + '"', service)
        self.assertIn("NeedsDateRange", service)

    def test_every_report_reads_posted_data_only(self):
        service = (APP / "Services" / "ReportService.cs").read_text(encoding="utf-8")
        # The four reconciliation views and ProfitLossService are the only sources.
        for source in ("v_stock_on_hand", "v_party_balance", "v_bank_balance",
                       "v_cheque_register", "ProfitLossService.Report"):
            self.assertIn(source, service)
        self.assertNotIn("journal_lines", service.replace("v_", ""))
        self.assertNotIn("is_posted", service)

    def test_profit_loss_report_table_has_the_summary_rows(self):
        service = (APP / "Services" / "ReportService.cs").read_text(encoding="utf-8")
        for label in ("سود ناخالص", "سود عملیاتی", "بهای کالای فروش‌رفته", "هزینه‌های عملیاتی"):
            self.assertIn(label, service)
        self.assertIn("report.OperatingProfitIrr", service)

    def test_daily_flow_report_uses_the_dashboards_own_query(self):
        service = (APP / "Services" / "ReportService.cs").read_text(encoding="utf-8")
        self.assertIn("DashboardQueryService.LoadDailyFlow", service)
        self.assertIn("DaysBetween", service)

    def test_second_display_is_read_only_and_self_refreshing(self):
        window = (APP / "Views" / "DashboardWindow.cs").read_text(encoding="utf-8")
        self.assertIn("_secondDisplay", window)
        self.assertIn("DispatcherTimer", window)
        self.assertIn("TimeSpan.FromSeconds(30)", window)
        self.assertIn("Topmost", window)
        self.assertIn("WindowState.Maximized", window)
        self.assertIn("IsReadOnly = true", window)
        # No write path may run for the hall display.
        self.assertIn("if (!_secondDisplay)", window)
        for forbidden in ("Commit(", "EnsureEntry", "PurchaseService", "SalesService",
                          "SettlementService", "JournalService.PostEntry"):
            self.assertNotIn(forbidden, window)

    def test_dashboard_kpis_are_posted_only(self):
        service = (APP / "Services" / "DashboardQueryService.cs").read_text(encoding="utf-8")
        for sql in ("d.status='APPROVED'", "je.is_posted=1"):
            self.assertIn(sql, service)

    def test_table_export_reuses_the_existing_permission_and_audit_run(self):
        service = (APP / "Import" / "ExportService.cs").read_text(encoding="utf-8")
        self.assertIn("ExportTableCsv", service)
        self.assertIn("SqlExportStart", service)
        self.assertIn("SqlExportFinish", service)
        # The same reports.view permission guards both export paths.
        self.assertEqual(service.count("RequirePermission(connection, transaction, userId);"), 2)

    def test_export_run_records_the_report_code_as_the_export_kind(self):
        service = (APP / "Import" / "ExportService.cs").read_text(encoding="utf-8")
        start = service[service.index("SqlExportStart"):service.index("SqlExportFinish")]
        self.assertIn("@kind", start)
        # Both export paths must pass a kind: the document export keeps its label.
        self.assertEqual(service.count('AddWithValue("@kind"'), 2)
        self.assertIn('"DELTA_EXCEL"', service)

    def test_export_table_writes_a_utf8_csv(self):
        service = (APP / "Import" / "ExportService.cs").read_text(encoding="utf-8")
        block = service[service.index("ExportTableCsv"):]
        self.assertIn("new UTF8Encoding(true)", block)
        self.assertIn("writer.WriteLine(Join(headers));", block)
        self.assertIn("foreach (System.Data.DataRow row in table.Rows)", block)
        self.assertIn("@rows", block)

    def test_reports_window_is_read_only_and_csv_only(self):
        window = (APP / "Views" / "ReportsWindow.cs").read_text(encoding="utf-8")
        self.assertIn("IsReadOnly = true", window)
        self.assertIn("ExportService.ExportTableCsv", window)
        self.assertIn("csv", window.lower())
        for forbidden in (".xlsx", "OpenXML", "Workbook"):
            self.assertNotIn(forbidden, window)

    def test_main_window_wires_dashboard_reports_and_second_display(self):
        xaml = (APP / "MainWindow.xaml").read_text(encoding="utf-8")
        code = (APP / "MainWindow.xaml.cs").read_text(encoding="utf-8")
        for handler in ("OpenDashboard_Click", "OpenReports_Click", "OpenSecondDisplay_Click"):
            self.assertIn(handler, xaml)
            self.assertIn(handler, code)
        self.assertIn("Views.DashboardWindow", code)
        self.assertIn("Views.ReportsWindow", code)

    def test_report_and_dashboard_permissions_are_seeded_and_gated(self):
        seed = (APP / "Database" / "system_seed.sql").read_text(encoding="utf-8")
        self.assertIn("reports.view", seed)
        self.assertIn("dashboard.view", seed)
        # The export path is guarded by reports.view only.
        export = (APP / "Import" / "ExportService.cs").read_text(encoding="utf-8")
        self.assertIn("rp.permission_code='reports.view'", export)
        # A role that holds reports.view can read the reports table.
        cursor = self.connection.execute(
            "SELECT COUNT(*) FROM permissions WHERE permission_code='reports.view'")
        self.assertEqual(cursor.fetchone()[0], 1)

    def test_second_display_never_calls_the_main_window_write_handlers(self):
        window = (APP / "Views" / "DashboardWindow.cs").read_text(encoding="utf-8")
        for handler in ("OpenQuickEntry", "Import_Click", "OpenAccounting_Click"):
            self.assertNotIn(handler, window)


if __name__ == "__main__":
    unittest.main()
