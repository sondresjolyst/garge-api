using garge_api.Models.Shop;
using garge_api.Models.Subscription;

namespace garge_api.Services
{
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
    }

    public class VippsOrderLine
    {
        public string Name { get; set; } = string.Empty;
        public string Id { get; set; } = string.Empty;
        public int UnitPriceInOre { get; set; }
        public int UnitPriceExclVatInOre { get; set; }
        public int Quantity { get; set; }
        public int TaxPercentage { get; set; }
    }

    public interface IVippsService
    {
        Task<string> GetAccessTokenAsync();

        Task<VippsCreateAgreementResponse> CreateAgreementAsync(
            Product product, string userId, string redirectUrl, string phoneNumber, int effectivePriceInOre);
        Task<VippsAgreementResponse> GetAgreementAsync(string agreementId);
        Task CancelAgreementAsync(string agreementId);

        Task<VippsCreatePaymentResponse> CreatePaymentAsync(
            Order order, List<VippsOrderLine> receiptLines, string redirectUrl, string phoneNumber);
        Task<VippsPaymentResponse> GetPaymentAsync(string reference);
        Task CapturePaymentAsync(string reference, int amountInOre);
        Task CancelPaymentAsync(string reference);

        Task<(string WebhookId, string Secret)> RegisterWebhookAsync(string url, string[] events);
        bool VerifyWebhookSignature(string rawBody, string signatureHeader, string secret);
    }
}
