namespace garge_api.Helpers
{
    public static class LogSanitizer
    {
        public static string Sanitize(string input) => input
            .Replace("\r", "", StringComparison.Ordinal)
            .Replace("\n", "", StringComparison.Ordinal);
    }
}
