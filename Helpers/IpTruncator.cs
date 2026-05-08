using System.Net;
using System.Net.Sockets;

namespace garge_api.Helpers
{
    /// <summary>
    /// Truncates IP addresses to /24 (IPv4) or /48 (IPv6) before persistence.
    /// Sufficient for proof-of-consent and fraud correlation while reducing
    /// household-level re-identification (the Norwegian Data Protection
    /// Authority recognizes this as pseudonymization for GDPR Art. 32 /
    /// Art. 5(1)(c)).
    /// </summary>
    public static class IpTruncator
    {
        public static string Truncate(string? ip)
        {
            if (string.IsNullOrWhiteSpace(ip)) return string.Empty;
            if (!IPAddress.TryParse(ip, out var addr)) return string.Empty;

            if (addr.AddressFamily == AddressFamily.InterNetwork)
            {
                var bytes = addr.GetAddressBytes();
                bytes[3] = 0;
                return new IPAddress(bytes).ToString();
            }

            if (addr.AddressFamily == AddressFamily.InterNetworkV6)
            {
                var bytes = addr.GetAddressBytes();
                for (int i = 6; i < bytes.Length; i++) bytes[i] = 0;
                return new IPAddress(bytes).ToString();
            }

            return string.Empty;
        }
    }
}
