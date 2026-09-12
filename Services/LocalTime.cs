namespace garge_api.Services
{
    /// <summary>
    /// Garge sells only in Norway, so times shown to users are Norwegian local time rather than the UTC
    /// the database stores. Falls back to UTC if the host carries no timezone database.
    /// </summary>
    public static class LocalTime
    {
        private static readonly TimeZoneInfo Zone = Resolve();

        /// <summary>Times keep a "UTC" suffix when the timezone database is missing, rather than reading an hour or two wrong with no warning.</summary>
        public static string Format(DateTime utc, string format = "yyyy-MM-dd HH:mm")
        {
            var text = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone).ToString(format);
            return Zone == TimeZoneInfo.Utc ? $"{text} UTC" : text;
        }

        private static TimeZoneInfo Resolve()
        {
            foreach (var id in new[] { "Europe/Oslo", "W. Europe Standard Time" })
            {
                try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
                catch (TimeZoneNotFoundException) { }
                catch (InvalidTimeZoneException) { }
            }
            return TimeZoneInfo.Utc;
        }
    }
}
