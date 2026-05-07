using garge_api.Models.Shop;
using garge_api.Models.Subscription;

namespace garge_api.Services
{
    public enum WebhookVerifyResult
    {
        Valid,
        MissingSecret,
        MissingHeader,
        BadDate,
        Stale,
        BadContentHash,
        BadSignature
    }

    public class VippsCreateAgreementResponse
    {
        public string AgreementId { get; set; } = string.Empty;
        public string VippsConfirmationUrl { get; set; } = string.Empty;
    }

    public class VippsAgreementResponse
    {
        public string Id { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string ProductName { get; set; } = string.Empty;
        public DateTime? Start { get; set; }
        public DateTime? Stop { get; set; }
    }

    public class VippsCreatePaymentResponse
    {
        public string RedirectUrl { get; set; } = string.Empty;
        public string Reference { get; set; } = string.Empty;
    }

    public class VippsPaymentResponse
    {
        public string Reference { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public string? ProfileSub { get; set; }
    }

    public class VippsUserInfo
    {
        public string? Name { get; set; }
        public string? Email { get; set; }
        public string? PhoneNumber { get; set; }
        public VippsAddress? Address { get; set; }
    }

    public class VippsAddress
    {
        public string? StreetAddress { get; set; }
        public string? PostalCode { get; set; }
        public string? Region { get; set; }
        public string? Country { get; set; }
        public string? Formatted { get; set; }
    }

    public class VippsOrderLine
    {
        public string Name { get; set; } = string.Empty;
        public string Id { get; set; } = string.Empty;
        public int UnitPriceInOre { get; set; }
        public int UnitPriceExclVatInOre { get; set; }
        public int Quantity { get; set; }
        /// <summary>VAT in basis points (Vipps spec: 0..10000, where 2500 = 25%).</summary>
        public int TaxPercentageBasisPoints { get; set; }
    }

    public interface IVippsService
    {
        Task<string> GetAccessTokenAsync();

        Task<VippsCreateAgreementResponse> CreateAgreementAsync(
            Product product, string userId, string redirectUrl, string phoneNumber,
            int effectivePriceInOre, string idempotencyKey);
        Task<VippsAgreementResponse> GetAgreementAsync(string agreementId);
        Task CancelAgreementAsync(string agreementId, string idempotencyKey);

        Task<VippsCreatePaymentResponse> CreatePaymentAsync(
            Order order, List<VippsOrderLine> receiptLines, string redirectUrl,
            string phoneNumber, string idempotencyKey);
        Task<VippsPaymentResponse> GetPaymentAsync(string reference);
        Task<VippsUserInfo?> GetUserInfoAsync(string sub);
        Task CapturePaymentAsync(string reference, int amountInOre, string idempotencyKey);
        Task CancelPaymentAsync(string reference, string idempotencyKey);

        Task<(string WebhookId, string Secret)> RegisterWebhookAsync(string url, string[] events);
        WebhookVerifyResult VerifyWebhookSignature(HttpRequest request, string rawBody, string secret);
    }
}
