namespace garge_api.Services
{
    public static class VippsAddressFormatter
    {
        public const int DefaultMaxLength = 500;

        public static string? Format(VippsAddress? address, int maxLength = DefaultMaxLength)
        {
            if (address == null) return null;

            var formatted = !string.IsNullOrEmpty(address.Formatted)
                ? address.Formatted
                : string.Join(", ", new[]
                {
                    address.StreetAddress,
                    address.PostalCode,
                    address.Region,
                    address.Country
                }.Where(s => !string.IsNullOrEmpty(s)));

            if (string.IsNullOrEmpty(formatted)) return null;
            return formatted.Length > maxLength ? formatted[..maxLength] : formatted;
        }
    }
}
