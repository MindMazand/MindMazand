using System;
using System.Globalization;
using System.Data.SQLite;

namespace Mazandasht.Desktop.Services
{
    /// <summary>
    /// Resolves the fiscal year that a document belongs to.
    ///
    /// The convention is owner-controlled and stored in operational_parameters
    /// under FISCAL_YEAR_KIND (JALALI or GREGORIAN). Until the owner decides,
    /// the Gregorian year is used, which is the pre-existing behaviour; the open
    /// finding O-1 records that this default still needs owner approval.
    /// </summary>
    public static class FiscalYearService
    {
        public const string ParameterCode = "FISCAL_YEAR_KIND";
        public const string Jalali = "JALALI";
        public const string Gregorian = "GREGORIAN";

        /// <summary>
        /// Returns the configured convention, defaulting to Gregorian.
        /// </summary>
        public static string Convention(string databasePath)
        {
            var value = BaseDataService.GetParameter(databasePath, ParameterCode);
            if (String.Equals(value, Jalali, StringComparison.OrdinalIgnoreCase)) return Jalali;
            if (String.Equals(value, Gregorian, StringComparison.OrdinalIgnoreCase)) return Gregorian;
            return Gregorian;
        }

        /// <summary>
        /// Fiscal year label for an ISO date (yyyy-MM-dd).
        /// </summary>
        public static string Resolve(string databasePath, string dateIso)
        {
            return ResolveValue(Convention(databasePath), dateIso);
        }

        /// <summary>
        /// Pure conversion used by the import engine and by the tests: it takes
        /// the convention explicitly so it needs no database.
        /// </summary>
        public static string ResolveValue(string convention, string dateIso)
        {
            if (!TryParseIso(dateIso, out var date))
                throw new ArgumentException("تاریخ معتبر نیست.");
            if (String.Equals(convention, Jalali, StringComparison.OrdinalIgnoreCase))
            {
                var calendar = new PersianCalendar();
                return calendar.GetYear(date).ToString("0000", CultureInfo.InvariantCulture);
            }
            return date.ToString("yyyy", CultureInfo.InvariantCulture);
        }

        public static bool TryParseIso(string value, out DateTime date)
        {
            return DateTime.TryParseExact((value ?? String.Empty).Trim(),
                "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
        }

        /// <summary>
        /// Converts a Jalali date written as yyyy/MM/dd or yyyy-MM-dd to ISO.
        /// </summary>
        public static string JalaliToIso(string value)
        {
            var text = (value ?? String.Empty).Trim().Replace('/', '-');
            var parts = text.Split('-');
            if (parts.Length != 3) throw new ArgumentException("تاریخ شمسی باید به شکل ۱۴۰۴/۰۸/۲۹ باشد.");
            int year, month, day;
            if (!Int32.TryParse(parts[0], out year) || !Int32.TryParse(parts[1], out month) ||
                !Int32.TryParse(parts[2], out day))
                throw new ArgumentException("تاریخ شمسی باید از اعداد تشکیل شده باشد.");
            if (year < 1200 || year > 1500) throw new ArgumentException("سال شمسی معتبر نیست.");
            try
            {
                var calendar = new PersianCalendar();
                var date = new DateTime(year, month, day, calendar);
                return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            catch (ArgumentOutOfRangeException)
            {
                throw new ArgumentException("تاریخ شمسی معتبر نیست.");
            }
        }

        /// <summary>
        /// Best-effort date parse for imported rows: ISO first, then Jalali.
        /// </summary>
        public static string ResolveDate(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) throw new ArgumentException("تاریخ الزامی است.");
            DateTime iso;
            if (TryParseIso(value, out iso)) return iso.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return JalaliToIso(value);
        }

        /// <summary>
        /// True when the owner has explicitly chosen the fiscal-year convention.
        /// </summary>
        public static bool IsConventionDecided(string databasePath)
        {
            using (var connection = new SQLiteConnection(
                "Data Source=" + databasePath + ";Version=3;Pooling=True;"))
            {
                connection.Open();
                using (var command = new SQLiteCommand(
                    "SELECT COUNT(*) FROM operational_parameters WHERE parameter_code=@code AND is_active=1;",
                    connection))
                {
                    command.Parameters.AddWithValue("@code", ParameterCode);
                    return Convert.ToInt32(command.ExecuteScalar()) == 1;
                }
            }
        }
    }
}
