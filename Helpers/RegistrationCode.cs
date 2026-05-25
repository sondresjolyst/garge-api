using System.Security.Cryptography;

namespace garge_api.Helpers
{
    /// <summary>
    /// Generates device registration codes from an unambiguous alphabet (no easily confused
    /// characters such as I/1, O/0). Shared by the sensor and switch controllers so both use the
    /// same alphabet and uniqueness loop.
    /// </summary>
    public static class RegistrationCode
    {
        /// <summary>Alphabet used for registration codes: excludes I, O, 0 and 1 to avoid ambiguity.</summary>
        public const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

        /// <summary>
        /// Produces a cryptographically random code of the requested length that does not already
        /// exist, as determined by the supplied <paramref name="existsAsync"/> predicate. The
        /// predicate is awaited for each candidate until a free code is found.
        /// </summary>
        /// <param name="existsAsync">Returns true when a candidate code is already taken.</param>
        /// <param name="length">The number of characters in the generated code.</param>
        public static async Task<string> GenerateUniqueAsync(Func<string, Task<bool>> existsAsync, int length = 10)
        {
            string code;
            bool exists;
            do
            {
                code = Generate(length);
                exists = await existsAsync(code);
            } while (exists);

            return code;
        }

        /// <summary>Produces a single cryptographically random code of the requested length.</summary>
        public static string Generate(int length = 10) =>
            new(Enumerable.Range(0, length)
                .Select(_ => Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)])
                .ToArray());
    }
}
