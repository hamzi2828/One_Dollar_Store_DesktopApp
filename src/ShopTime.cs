using System;
using System.Globalization;
using System.Threading;

namespace DollarCoAttendance
{
    // Shop time is Pakistan time (Asia/Karachi, UTC+5 all year, no daylight
    // saving). Every time on screen is shown in PKT whatever zone the PC's own
    // clock is set to, and "now" is the SERVER's clock as far as it is known
    // (ApiClient reads the Date header of every answer), so a counter PC whose
    // clock is wrong still shows the right time.
    public static class ShopTime
    {
        private static readonly TimeSpan PktOffset = TimeSpan.FromHours(5);

        // Server clock minus this PC's clock, in ticks (Interlocked: set from
        // whichever thread made a call).
        private static long serverOffsetTicks;
        private static int offsetKnown;

        public static TimeSpan ServerOffset
        {
            get { return TimeSpan.FromTicks(Interlocked.Read(ref serverOffsetTicks)); }
        }

        public static bool OffsetKnown { get { return offsetKnown == 1; } }

        public static void SetServerTime(DateTime serverUtc)
        {
            TimeSpan offset = serverUtc.ToUniversalTime() - DateTime.UtcNow;
            Interlocked.Exchange(ref serverOffsetTicks, offset.Ticks);
            Interlocked.Exchange(ref offsetKnown, 1);
        }

        // The best idea of "now": the server's clock once it has answered.
        public static DateTime NowUtc
        {
            get { return DateTime.UtcNow + ServerOffset; }
        }

        // A UTC instant as a PKT wall-clock time (Kind Unspecified).
        public static DateTime ToShop(DateTime utc)
        {
            DateTime u = utc.Kind == DateTimeKind.Local ? utc.ToUniversalTime() : utc;
            return DateTime.SpecifyKind(u + PktOffset, DateTimeKind.Unspecified);
        }

        // "9:58 am"
        public static string Clock(DateTime utc)
        {
            DateTime t = ToShop(utc);
            int h = t.Hour % 12;
            if (h == 0) h = 12;
            return h + ":" + t.Minute.ToString("00") + (t.Hour >= 12 ? " pm" : " am");
        }

        public static string Clock(DateTime? utc)
        {
            return utc.HasValue ? Clock(utc.Value) : string.Empty;
        }

        // 12 -> "12 min", 455 -> "7 h 35 m", 60 -> "1 h" (the server's wording).
        public static string Duration(int minutes)
        {
            int m = Math.Max(0, minutes);
            int h = m / 60;
            if (h == 0) return m + " min";
            return m % 60 == 0 ? h + " h" : h + " h " + (m % 60) + " m";
        }

        // 'YYYY-MM-DD' of the shop day an instant falls on.
        public static string DayKey(DateTime utc)
        {
            return ToShop(utc).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        public static string TodayKey
        {
            get { return DayKey(NowUtc); }
        }

        // "Mon 19 Oct"
        public static string DayLabel(DateTime utc)
        {
            return ToShop(utc).ToString("ddd d MMM", CultureInfo.InvariantCulture);
        }

        // "2026-10-19" -> "Mon 19 Oct"; the key itself if it does not parse.
        public static string DayLabel(string dayKey)
        {
            DateTime d;
            if (DateTime.TryParseExact(dayKey ?? string.Empty, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out d))
                return d.ToString("ddd d MMM", CultureInfo.InvariantCulture);
            return dayKey ?? string.Empty;
        }

        // An ISO time from the server ("2026-10-19T04:58:00.000Z") as UTC.
        public static DateTime? ParseUtc(string iso)
        {
            if (string.IsNullOrEmpty(iso)) return null;
            DateTime value;
            if (DateTime.TryParse(iso, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out value))
                return DateTime.SpecifyKind(value, DateTimeKind.Utc);
            return null;
        }

        public static string ToIso(DateTime utc)
        {
            DateTime u = utc.Kind == DateTimeKind.Local ? utc.ToUniversalTime() : utc;
            return DateTime.SpecifyKind(u, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        }
    }
}
