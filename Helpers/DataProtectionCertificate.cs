using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;

namespace garge_api.Helpers
{
    /// <summary>
    /// Encrypts the Data Protection key ring with the certificate in <c>DataProtection:Certificate</c>, a base64
    /// PKCS#12 file without a password, so a copy of the database alone does not expose the keys.
    /// </summary>
    public static class DataProtectionCertificate
    {
        public const string ConfigKey = "DataProtection:Certificate";

        /// <summary>
        /// Protects new keys with the configured certificate and reads keys it protected. Keys stored before it was set
        /// stay readable. With <paramref name="required"/> a missing certificate stops the app instead.
        /// </summary>
        public static IDataProtectionBuilder ProtectKeysWithConfiguredCertificate(
            this IDataProtectionBuilder builder, IConfiguration config, bool required)
        {
            var encoded = config[ConfigKey];
            if (string.IsNullOrWhiteSpace(encoded))
            {
                if (required) throw new InvalidOperationException($"{ConfigKey} is not set, so the Data Protection keys would be stored unencrypted.");
                return builder;
            }

            var certificate = Load(encoded);
            return builder.ProtectKeysWithCertificate(certificate).UnprotectKeysWithAnyCertificate(certificate);
        }

        internal static X509Certificate2 Load(string encoded)
        {
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(encoded.Trim());
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException($"{ConfigKey} is not valid base64.", ex);
            }

            var certificate = X509CertificateLoader.LoadPkcs12(bytes, null, X509KeyStorageFlags.EphemeralKeySet);
            if (!certificate.HasPrivateKey) throw new InvalidOperationException($"{ConfigKey} has no private key.");
            return certificate;
        }
    }
}
