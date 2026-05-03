using garge_api.Models;
using garge_api.Models.Push;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace garge_api.Services
{
    public interface IWebPushService
    {
        Task SendAsync(string userId, string title, string body, CancellationToken ct = default);
    }

    public class WebPushService(
        ApplicationDbContext db,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<WebPushService> logger) : IWebPushService
    {
        public async Task SendAsync(string userId, string title, string body, CancellationToken ct = default)
        {
            var publicKey = configuration["Vapid:PublicKey"] ?? string.Empty;
            var privateKey = configuration["Vapid:PrivateKey"] ?? string.Empty;
            var subject = configuration["Vapid:Subject"] ?? "mailto:admin@garge.no";

            if (string.IsNullOrEmpty(publicKey) || string.IsNullOrEmpty(privateKey))
            {
                logger.LogWarning("VAPID keys not configured — push skipped");
                return;
            }

            var subscriptions = await db.PushSubscriptions
                .Where(s => s.UserId == userId)
                .ToListAsync(ct);

            if (subscriptions.Count == 0) return;

            var payload = JsonSerializer.SerializeToUtf8Bytes(new { title, body });
            var toRemove = new List<int>();
            int successCount = 0;
            Exception? lastError = null;

            foreach (var sub in subscriptions)
            {
                try
                {
                    await SendToSubscriptionAsync(sub, payload, publicKey, privateKey, subject, ct);
                    logger.LogInformation("Push sent: subscription {SubId} user {UserId}", sub.Id, userId);
                    successCount++;
                }
                catch (HttpRequestException ex) when (
                    ex.StatusCode == System.Net.HttpStatusCode.Gone ||
                    ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    logger.LogInformation("Stale subscription {SubId} removed for user {UserId}", sub.Id, userId);
                    toRemove.Add(sub.Id);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Push failed: subscription {SubId} user {UserId}", sub.Id, userId);
                    lastError = ex;
                }
            }

            if (toRemove.Count > 0)
            {
                db.PushSubscriptions.RemoveRange(db.PushSubscriptions.Where(s => toRemove.Contains(s.Id)));
                await db.SaveChangesAsync(ct);
            }

            if (successCount == 0 && lastError != null)
                throw lastError;
        }

        private async Task SendToSubscriptionAsync(
            PushSubscription sub,
            byte[] payload,
            string vapidPublicKey,
            string vapidPrivateKey,
            string vapidSubject,
            CancellationToken ct)
        {
            var p256dh = Base64UrlDecode(sub.P256dh);
            var auth = Base64UrlDecode(sub.Auth);

            var encrypted = Encrypt(payload, p256dh, auth);
            var jwt = BuildVapidJwt(sub.Endpoint, vapidPublicKey, vapidPrivateKey, vapidSubject);

            var client = httpClientFactory.CreateClient("webpush");
            var request = new HttpRequestMessage(HttpMethod.Post, sub.Endpoint);
            request.Headers.TryAddWithoutValidation("Authorization", $"vapid t={jwt},k={vapidPublicKey}");
            request.Headers.TryAddWithoutValidation("TTL", "86400");
            request.Content = new ByteArrayContent(encrypted);
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            request.Content.Headers.TryAddWithoutValidation("Content-Encoding", "aes128gcm");

            var response = await client.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
        }

        // RFC 8291 content encryption (aes128gcm)
        private static byte[] Encrypt(byte[] payload, byte[] receiverPublicKey, byte[] authSecret)
        {
            using var senderEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

            var receiverParams = new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint
                {
                    X = receiverPublicKey[1..33],
                    Y = receiverPublicKey[33..65]
                }
            };
            using var receiverEcdh = ECDiffieHellman.Create(receiverParams);

            var sharedSecret = senderEcdh.DeriveRawSecretAgreement(receiverEcdh.PublicKey);

            var senderParams = senderEcdh.ExportParameters(false);
            var senderPublicKey = new byte[65];
            senderPublicKey[0] = 0x04;
            senderParams.Q.X!.CopyTo(senderPublicKey, 1);
            senderParams.Q.Y!.CopyTo(senderPublicKey, 33);

            var salt = RandomNumberGenerator.GetBytes(16);

            // key_info = "WebPush: info\x00" || ua_public || as_public
            var keyInfo = new byte[14 + 65 + 65];
            "WebPush: info\0"u8.CopyTo(keyInfo.AsSpan());
            receiverPublicKey.CopyTo(keyInfo.AsSpan(14));
            senderPublicKey.CopyTo(keyInfo.AsSpan(79));

            // IKM derivation
            var prkKey = HKDF.Extract(HashAlgorithmName.SHA256, sharedSecret, authSecret);
            var ikm = HKDF.Expand(HashAlgorithmName.SHA256, prkKey, 32, keyInfo);

            // Content key derivation
            var prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt);
            var cek = HKDF.Expand(HashAlgorithmName.SHA256, prk, 16, "Content-Encoding: aes128gcm\0"u8.ToArray());
            var nonce = HKDF.Expand(HashAlgorithmName.SHA256, prk, 12, "Content-Encoding: nonce\0"u8.ToArray());

            // Plaintext = payload || 0x02 (final record delimiter)
            var plaintext = new byte[payload.Length + 1];
            payload.CopyTo(plaintext, 0);
            plaintext[^1] = 0x02;

            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[16];
            using var aesGcm = new AesGcm(cek, 16);
            aesGcm.Encrypt(nonce, plaintext, ciphertext, tag);

            // aes128gcm header: salt(16) || rs(4) || idlen(1) || keyid(65) || ciphertext || tag
            var result = new byte[16 + 4 + 1 + 65 + ciphertext.Length + 16];
            var span = result.AsSpan();
            int pos = 0;
            salt.CopyTo(span[pos..]); pos += 16;
            span[pos++] = 0x00; span[pos++] = 0x00; span[pos++] = 0x10; span[pos++] = 0x00; // rs = 4096
            span[pos++] = 65; // idlen
            senderPublicKey.CopyTo(span[pos..]); pos += 65;
            ciphertext.CopyTo(span[pos..]); pos += ciphertext.Length;
            tag.CopyTo(span[pos..]);

            return result;
        }

        // RFC 8292 VAPID JWT (ES256)
        private static string BuildVapidJwt(string endpoint, string publicKeyBase64Url, string privateKeyBase64Url, string subject)
        {
            var audience = new Uri(endpoint).GetLeftPart(UriPartial.Authority);

            var pubBytes = Base64UrlDecode(publicKeyBase64Url);
            var privBytes = Base64UrlDecode(privateKeyBase64Url);

            var ecParams = new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                D = privBytes,
                Q = new ECPoint
                {
                    X = pubBytes[1..33],
                    Y = pubBytes[33..65]
                }
            };
            using var ecDsa = ECDsa.Create(ecParams);

            var secKey = new ECDsaSecurityKey(ecDsa);
            var creds = new SigningCredentials(secKey, SecurityAlgorithms.EcdsaSha256)
            {
                // Disable caching: the provider factory caches by key content and holds a reference
                // to the ECDsa, which gets disposed at the end of this method on the next call.
                CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false }
            };

            var handler = new JwtSecurityTokenHandler();
            var token = handler.CreateJwtSecurityToken(
                issuer: null,
                audience: audience,
                subject: new ClaimsIdentity([new Claim("sub", subject)]),
                notBefore: null,
                expires: DateTime.UtcNow.AddHours(12),
                issuedAt: DateTime.UtcNow,
                signingCredentials: creds);

            return handler.WriteToken(token);
        }

        private static byte[] Base64UrlDecode(string input)
        {
            input = input.Replace('-', '+').Replace('_', '/');
            input += (input.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
            return Convert.FromBase64String(input);
        }
    }
}
