using System.Text.RegularExpressions;

namespace garge_api.Helpers
{
    public static class PhoneNumber
    {
        private static readonly Regex Cleaner = new(@"\D", RegexOptions.Compiled);

        public static bool TryNormalizeNo(string raw, out string msisdn)
        {
            msisdn = string.Empty;
            if (string.IsNullOrWhiteSpace(raw)) return false;

            var digits = Cleaner.Replace(raw, string.Empty);
            if (digits.Length == 8)
                digits = "47" + digits;
            if (digits.Length != 10 || !digits.StartsWith("47"))
                return false;

            msisdn = digits;
            return true;
        }
    }
}
