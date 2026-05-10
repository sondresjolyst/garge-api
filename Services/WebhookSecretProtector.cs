using Microsoft.AspNetCore.DataProtection;

namespace garge_api.Services
{
    public interface IWebhookSecretProtector
    {
        string Protect(string plaintext);
        string Unprotect(string stored);
    }

    public class WebhookSecretProtector : IWebhookSecretProtector
    {
        private const string Prefix = "enc:v1:";
        private readonly IDataProtector _protector;

        public WebhookSecretProtector(IDataProtectionProvider provider)
        {
            _protector = provider.CreateProtector("garge.vipps.webhook-secret.v1");
        }

        public string Protect(string plaintext) =>
            string.IsNullOrEmpty(plaintext) ? plaintext : Prefix + _protector.Protect(plaintext);

        public string Unprotect(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return stored;
            return stored.StartsWith(Prefix, StringComparison.Ordinal)
                ? _protector.Unprotect(stored[Prefix.Length..])
                : stored;
        }
    }
}
