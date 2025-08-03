namespace garge_api.Services
{
    public static class LogSanitizer
    {
        public static string Sanitize(string input, int maxLength = 100)
        {
            if (string.IsNullOrEmpty(input)) return "";
            var sanitized = new string(input.Where(c => !char.IsControl(c)).ToArray());
            if (sanitized.Length > maxLength)
                sanitized = sanitized.Substring(0, maxLength) + "...";
            return sanitized;
        }

        public static string MaskUrlDomain(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "";
            var sanitized = url.Replace("\r", "").Replace("\n", "");
            try
            {
                var uri = new Uri(sanitized);
                return uri.Host;
            }
            catch
            {
                return "***invalid-url***";
            }
        }
    }
}
