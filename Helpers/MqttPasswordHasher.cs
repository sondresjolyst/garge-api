using System.Security.Cryptography;
using System.Text;

namespace garge_api.Helpers
{
    /// <summary>
    /// PBKDF2 hashing for EMQX broker users. Must match the broker's password_hash
    /// configuration (SHA-512, 300k iterations, lowercase hex).
    /// </summary>
    internal static class MqttPasswordHasher
    {
        internal static string GenerateSalt(int length = 16)
        {
            var saltBytes = new byte[length];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(saltBytes);
            return Convert.ToHexString(saltBytes).ToLowerInvariant();
        }

        internal static string HashPasswordPBKDF2(string password, string salt, int iterations = 300_000, int hashByteSize = 32)
        {
            var saltBytes = Encoding.UTF8.GetBytes(salt);
            var hash = Rfc2898DeriveBytes.Pbkdf2(password, saltBytes, iterations, HashAlgorithmName.SHA512, hashByteSize);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
    }
}
