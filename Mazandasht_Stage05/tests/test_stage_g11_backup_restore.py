# -*- coding: utf-8 -*-
"""Stage G11 — security, backup and restore.

Acceptance criteria covered:
  * backup profiles/runs with retention, SHA-256 and a verified health check;
  * restore needs manager approval, the exact confirmation phrase, a healthy
    backup and a PRE_RESTORE safety copy of the live database;
  * user administration is users.manage gated, the password policy is enforced
    in one place, and login success/failure write security events;
  * the schema enums accept exactly the reasons/event types the services write.
"""
import pathlib
import re
import sqlite3
import unittest
import uuid as _uuid

ROOT = pathlib.Path(__file__).resolve().parents[1]
APP = ROOT / "app" / "Mazandasht.Desktop"
DB = APP / "Database"
MIGRATIONS = [DB / name for name in (
    "migration_002.sql", "migration_003.sql", "migration_004.sql",
    "migration_005.sql", "migration_006.sql", "migration_007.sql")]


def uuid4hex():
    return _uuid.uuid4().hex


def database():
    connection = sqlite3.connect(":memory:")
    connection.execute("PRAGMA foreign_keys=ON")
    connection.executescript((DB / "schema_v1.sql").read_text(encoding="utf-8"))
    connection.executescript((DB / "system_seed.sql").read_text(encoding="utf-8"))
    for migration in MIGRATIONS:
        connection.executescript(migration.read_text(encoding="utf-8"))
    connection.commit()
    return connection


def add_role(connection, role_code, permissions):
    role_id = "role-" + uuid4hex()
    connection.execute(
        "INSERT INTO roles(id,role_code,display_name,is_active) VALUES(?,?,?,1)",
        (role_id, role_code, role_code))
    for permission in permissions:
        connection.execute(
            "INSERT INTO role_permissions(role_id,permission_code,granted) VALUES(?,?,1)",
            (role_id, permission))
    return role_id


def add_user(connection, username, role_code, iterations=100000):
    """Adds a user bound to an existing (seeded) role code."""
    user_id = "user-" + uuid4hex()
    connection.execute(
        "INSERT INTO users(id,username,display_name,password_hash,password_salt,"
        "password_iterations,role_id,is_active,must_change_password) "
        "VALUES(?,?,?,?,?,?,"
        "(SELECT id FROM roles WHERE role_code=?),1,0)",
        (user_id, username, username, b"x", b"y", iterations, role_code))
    connection.commit()
    return user_id


class StageG11BackupRestore(unittest.TestCase):
    def setUp(self):
        self.connection = database()

    def tearDown(self):
        self.connection.close()

    # ------------------------------------------------------------------
    # schema contract
    def test_backup_tables_match_the_documented_columns_and_reasons(self):
        columns = {row[1] for row in self.connection.execute("PRAGMA table_info(backup_profiles)")}
        for column in ("profile_name", "destination_path", "keep_last_n",
                       "keep_monthly_n", "backup_on_logout", "backup_on_exit", "is_active"):
            self.assertIn(column, columns)
        runs = {row[1] for row in self.connection.execute("PRAGMA table_info(backup_runs)")}
        for column in ("profile_id", "reason", "file_path", "file_size_bytes",
                       "sha256", "status", "created_by", "started_at_utc",
                       "completed_at_utc", "error_text"):
            self.assertIn(column, runs)
        schema = "".join(path.read_text(encoding="utf-8")
                         for path in [DB / "schema_v1.sql"] + MIGRATIONS)
        reason_check = re.search(r"reason\s+TEXT\s+NOT NULL\s+CHECK\s*\(reason IN \(([^)]*)\)\)",
                                 schema, re.S).group(1)
        allowed = {value.strip().strip("'") for value in reason_check.split(",")}
        self.assertEqual(allowed, {"LOGOUT", "APP_EXIT", "MANUAL",
                                   "PRE_MIGRATION", "SCHEDULED", "PRE_RESTORE"})

    def test_security_event_enum_accepts_the_codes_the_services_write(self):
        schema = (DB / "schema_v1.sql").read_text(encoding="utf-8")
        event_check = re.search(r"event_type\s+TEXT\s+NOT NULL\s+CHECK\s*\(event_type IN \(([^)]*)\)\)",
                                schema, re.S).group(1)
        allowed = {value.strip().strip("'") for value in event_check.split(",")}
        auth = (APP / "Services" / "AuthService.cs").read_text(encoding="utf-8")
        users = (APP / "Services" / "UserAdminService.cs").read_text(encoding="utf-8")
        for code in ("LOGIN_SUCCESS", "LOGIN_FAILURE"):
            self.assertIn('"' + code + '"', auth)
            self.assertIn(code, allowed)
        for code in ("PASSWORD_CHANGED", "USER_DISABLED"):
            self.assertIn('"' + code + '"', users)
            self.assertIn(code, allowed)

    # ------------------------------------------------------------------
    # backup service
    def test_backup_service_is_gated_by_backup_manage(self):
        source = (APP / "Services" / "BackupService.cs").read_text(encoding="utf-8")
        self.assertIn("rp.permission_code='backup.manage'", source)
        self.assertIn("RequirePermission(connection, transaction, userId)", source)
        self.assertIn("UnauthorizedAccessException", source)

    def test_backup_uses_sqlite_online_backup_not_a_raw_file_copy(self):
        source = (APP / "Services" / "BackupService.cs").read_text(encoding="utf-8")
        self.assertIn("source.BackupDatabase(destination, \"main\", \"main\", -1, null, 0);", source)
        # The live database must never be copied byte-for-byte.
        body = source[source.index("private static void CreateConsistentBackup"):]
        body = body[:body.index("static bool DatabaseIsHealthy")]
        self.assertNotIn("File.Copy", body)

    def test_backup_run_lifecycle_is_recorded(self):
        source = (APP / "Services" / "BackupService.cs").read_text(encoding="utf-8")
        for status in ("'STARTED'", "'SUCCESS'", "'FAILED'", "'VERIFIED'"):
            self.assertIn(status, source)
        self.assertIn("file_size_bytes", source)
        self.assertIn("sha256", source)
        self.assertIn("Sha256File", source)

    def test_retention_keeps_the_newest_and_one_per_month(self):
        source = (APP / "Services" / "BackupService.cs").read_text(encoding="utf-8")
        self.assertIn("ApplyRetention", source)
        self.assertIn("keepLastN", source)
        self.assertIn("keepMonthlyN", source)
        # The file just written is protected from deletion.
        self.assertIn("protectedFile", source)
        self.assertIn("keep.Add(Path.GetFullPath(protectedFile));", source)
        self.assertIn("yyyy-MM", source)

    def test_backup_verification_rechecks_the_database(self):
        source = (APP / "Services" / "BackupService.cs").read_text(encoding="utf-8")
        self.assertIn("PRAGMA integrity_check", source)
        self.assertIn("PRAGMA foreign_key_check", source)
        self.assertIn("PRAGMA user_version", source)

    def test_exit_and_logout_backups_only_touch_flagged_profiles(self):
        source = (APP / "Services" / "BackupService.cs").read_text(encoding="utf-8")
        self.assertIn("backup_on_exit=1", source)
        self.assertIn("backup_on_logout=1", source)
        self.assertIn("RunLogoutBackups", source)
        self.assertIn("RunExitBackups", source)
        # A failing profile must not stop the others.
        self.assertIn("failures.Add(ex.Message);", source)

    # ------------------------------------------------------------------
    # restore service
    def test_restore_needs_manager_approval_and_the_exact_phrase(self):
        source = (APP / "Services" / "RestoreService.cs").read_text(encoding="utf-8")
        self.assertIn("r.role_code IN ('ADMIN','MANAGER')", source)
        self.assertIn("ConfirmationPhrase", source)
        self.assertIn("String.Equals(confirmation, ConfirmationPhrase, StringComparison.Ordinal)",
                      source)
        self.assertIn("RequireApprover", source)

    def test_restore_verifies_the_backup_before_touching_the_live_database(self):
        source = (APP / "Services" / "RestoreService.cs").read_text(encoding="utf-8")
        self.assertIn("BackupService.DatabaseIsHealthy(fullBackupPath)", source)
        self.assertLess(source.index("BackupService.DatabaseIsHealthy(fullBackupPath)"),
                        source.index("WriteBackup(fullDatabasePath, safetyPath)"))

    def test_restore_keeps_a_pre_restore_safety_copy(self):
        source = (APP / "Services" / "RestoreService.cs").read_text(encoding="utf-8")
        self.assertIn("'PRE_RESTORE'", source)
        self.assertIn("mazandasht-PRE_RESTORE-", source)
        self.assertIn("BackupService.WriteBackup(fullDatabasePath, safetyPath)", source)

    def test_restore_stages_a_copy_so_the_original_backup_survives(self):
        source = (APP / "Services" / "RestoreService.cs").read_text(encoding="utf-8")
        self.assertIn("File.Copy(fullBackupPath, staged, false);", source)
        self.assertIn("File.Move(staged, fullDatabasePath);", source)
        # The staged copy is verified before the swap.
        self.assertLess(source.index("File.Copy(fullBackupPath, staged, false);"),
                        source.index("BackupService.DatabaseIsHealthy(staged)"))
        # The original backup file is never moved or deleted.
        self.assertNotIn("File.Delete(fullBackupPath)", source)
        self.assertNotIn("File.Move(fullBackupPath", source)

    def test_restore_swaps_atomically_and_rolls_back(self):
        source = (APP / "Services" / "RestoreService.cs").read_text(encoding="utf-8")
        self.assertIn("SQLiteConnection.ClearAllPools();", source)
        self.assertIn("PRAGMA wal_checkpoint(TRUNCATE);", source)
        self.assertIn("File.Move(fullDatabasePath, oldDatabase);", source)
        self.assertIn("File.Move(oldDatabase, fullDatabasePath);", source)
        self.assertIn("DeleteSidecars", source)

    # ------------------------------------------------------------------
    # security / user administration
    def test_user_administration_is_users_manage_gated(self):
        source = (APP / "Services" / "UserAdminService.cs").read_text(encoding="utf-8")
        self.assertIn("rp.permission_code='users.manage'", source)
        self.assertIn("HasUserManagement", source)
        self.assertIn("RoleOptions", source)

    def test_password_policy_is_enforced_in_one_place(self):
        source = (APP / "Services" / "UserAdminService.cs").read_text(encoding="utf-8")
        self.assertIn("public static class PasswordPolicy", source)
        self.assertIn("MinimumPasswordLength = 12", source)
        self.assertIn("Char.IsLetter", source)
        self.assertIn("Char.IsDigit", source)
        self.assertIn("IndexOf(username, StringComparison.OrdinalIgnoreCase)", source)
        # Every entry point goes through the same policy.
        self.assertEqual(source.count("PasswordPolicy.ThrowIfWeak("), 3)
        auth = (APP / "Services" / "AuthService.cs").read_text(encoding="utf-8")
        self.assertIn("UserAdminService.PasswordPolicy.ThrowIfWeak(username, password)", auth)

    def test_last_active_admin_cannot_be_disabled(self):
        source = (APP / "Services" / "UserAdminService.cs").read_text(encoding="utf-8")
        self.assertIn("SqlActiveAdminCount", source)
        self.assertIn("admins <= 1", source)
        self.assertIn("نمی‌توان تنها مدیر فعال سیستم را غیرفعال کرد", source)
        self.assertIn("نمی‌توانید حساب خود را غیرفعال کنید", source)

    def test_passwords_are_never_written_to_the_audit_log(self):
        for name in ("Services/UserAdminService.cs", "Services/AuthService.cs",
                     "Services/BackupService.cs", "Services/RestoreService.cs"):
            source = (APP / name).read_text(encoding="utf-8")
            audit = source[source.index("INSERT INTO audit_log"):]
            audit = audit[:audit.index(";")]
            self.assertNotIn("password", audit.lower(), name)
            self.assertNotIn("hash", audit.lower(), name)
            self.assertNotIn("salt", audit.lower(), name)

    def test_login_writes_security_events_without_blocking(self):
        source = (APP / "Services" / "AuthService.cs").read_text(encoding="utf-8")
        self.assertIn("WriteSecurityEvent", source)
        self.assertIn("reason=unknown_user", source)
        self.assertIn("reason=bad_password", source)
        self.assertIn("role=\" + roleCode", source)
        # Auditing must never break a login.
        self.assertIn("catch", source[source.index("private static void WriteSecurityEvent"):])

    # ------------------------------------------------------------------
    # UI wiring
    def test_backup_and_security_windows_are_wired_into_the_navigation(self):
        xaml = (APP / "MainWindow.xaml").read_text(encoding="utf-8")
        code = (APP / "MainWindow.xaml.cs").read_text(encoding="utf-8")
        for handler in ("OpenBackup_Click", "OpenSecurity_Click", "Logout_Click"):
            self.assertIn(handler, xaml)
            self.assertIn(handler, code)
        self.assertIn("Views.BackupWindow", code)
        self.assertIn("Views.SecurityWindow", code)

    def test_exit_and_logout_run_the_flagged_backups(self):
        code = (APP / "MainWindow.xaml.cs").read_text(encoding="utf-8")
        self.assertIn("BackupService.RunExitBackups", code)
        self.assertIn("BackupService.RunLogoutBackups", code)
        self.assertIn("Closing += MainWindow_Closing;", code)
        self.assertIn("AppSession.CurrentUser = null;", code)

    def test_restore_button_stays_disabled_without_permission_and_phrase(self):
        window = (APP / "Views" / "BackupWindow.cs").read_text(encoding="utf-8")
        self.assertIn("RestoreService.ConfirmationPhrase", window)
        self.assertIn("File.Exists(_restorePath.Text)", window)
        self.assertIn("_restoreButton.IsEnabled = _canManage &&", window)

    def test_security_window_disables_admin_actions_for_non_managers(self):
        window = (APP / "Views" / "SecurityWindow.cs").read_text(encoding="utf-8")
        self.assertIn("HasUserManagement", window)
        self.assertIn("_createButton.IsEnabled = _canManage;", window)
        self.assertIn("_disableButton.IsEnabled = _canManage && hasSelection;", window)
        self.assertIn("_resetButton.IsEnabled = _canManage && hasSelection;", window)

    # ------------------------------------------------------------------
    # real database behaviour for the parts SQLite owns
    def test_backup_and_restore_reason_and_status_are_database_enforced(self):
        profile = "profile-" + uuid4hex()
        self.connection.execute(
            "INSERT INTO backup_profiles(id,profile_name,destination_path,keep_last_n,"
            "keep_monthly_n,backup_on_logout,backup_on_exit,is_active,created_by) "
            "VALUES(?,?,?,?,?,?,?,?,?)",
            (profile, "پشتیبان نمونه", "/tmp/backup", 30, 3, 1, 1, 1, None))
        run = "run-" + uuid4hex()
        self.connection.execute(
            "INSERT INTO backup_runs(id,profile_id,reason,file_path,status,created_by) "
            "VALUES(?,?,?,?,?,?)",
            (run, profile, "MANUAL", "/tmp/backup/a.db", "SUCCESS", None))
        self.connection.commit()
        with self.assertRaises(sqlite3.IntegrityError):
            self.connection.execute(
                "INSERT INTO backup_runs(id,profile_id,reason,file_path,status,created_by) "
                "VALUES(?,?,?,?,?,?)",
                ("run-bad", profile, "NOT_A_REASON", "/tmp/backup/b.db", "SUCCESS", None))
        with self.assertRaises(sqlite3.IntegrityError):
            self.connection.execute(
                "INSERT INTO backup_runs(id,profile_id,reason,file_path,status,created_by) "
                "VALUES(?,?,?,?,?,?)",
                ("run-bad2", profile, "MANUAL", "/tmp/backup/c.db", "NOT_A_STATUS", None))
        row = self.connection.execute(
            "SELECT reason,status FROM backup_runs WHERE id=?", (run,)).fetchone()
        self.assertEqual(row, ("MANUAL", "SUCCESS"))

    def test_manager_role_can_approve_a_restore(self):
        # The seeded roles already carry backup.manage for ADMIN and MANAGER.
        admin_id = add_user(self.connection, "boss", "ADMIN")
        manager_id = add_user(self.connection, "foreman", "MANAGER")
        clerk_id = add_user(self.connection, "clerk1", "SUPERVISOR")
        query = ("SELECT COUNT(*) FROM users u JOIN roles r ON r.id=u.role_id "
                 "WHERE u.id=? AND u.is_active=1 AND r.is_active=1 "
                 "AND r.role_code IN ('ADMIN','MANAGER')")
        self.assertEqual(self.connection.execute(query, (admin_id,)).fetchone()[0], 1)
        self.assertEqual(self.connection.execute(query, (manager_id,)).fetchone()[0], 1)
        self.assertEqual(self.connection.execute(query, (clerk_id,)).fetchone()[0], 0)

    def test_users_manage_is_required_for_user_administration(self):
        admin_id = add_user(self.connection, "boss2", "ADMIN")
        clerk_id = add_user(self.connection, "clerk2", "SUPERVISOR")
        query = ("SELECT COUNT(*) FROM users u JOIN roles r ON r.id=u.role_id "
                 "JOIN role_permissions rp ON rp.role_id=r.id "
                 "WHERE u.id=? AND u.is_active=1 AND r.is_active=1 "
                 "AND rp.permission_code='users.manage' AND rp.granted=1")
        self.assertEqual(self.connection.execute(query, (admin_id,)).fetchone()[0], 1)
        self.assertEqual(self.connection.execute(query, (clerk_id,)).fetchone()[0], 0)


if __name__ == "__main__":
    unittest.main()
