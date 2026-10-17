using garge_api.Models.Shop;
using garge_api.Models.Subscription;

namespace garge_api.Services
{
    /// <summary>One line of a shop order, as the receipt in Vipps shows it.</summary>
    public class VippsOrderLine
    {
        public string Name { get; set; } = string.Empty;
        public string Id { get; set; } = string.Empty;
        public int UnitPriceInOre { get; set; }
        public int UnitPriceExclVatInOre { get; set; }
        public int Quantity { get; set; }
        public int TaxPercentageBasisPoints { get; set; }
    }

    /// <summary>The Vipps calls garge makes, in terms of its own products, orders and requests.</summary>
    public interface IVippsService
    {
        Task<VippsCreateAgreementResponse> CreateAgreementAsync(
            Product product, string userId, string redirectUrl, string phoneNumber,
            int unitPriceInOre, int quantity, string idempotencyKey);
        // Calls on an existing payment or agreement take the environment it was created in. Null means
        // the current test mode, for calls that create something new.
        Task<VippsAgreement> GetAgreementAsync(string agreementId, bool? isTest = null);
        Task CancelAgreementAsync(string agreementId, string idempotencyKey, bool? isTest = null);
        Task UpdateAgreementMaxAmountAsync(string agreementId, int newMaxAmountInOre, string idempotencyKey, bool? isTest = null);

        Task<VippsCreateChargeResponse> CreateChargeAsync(
            string agreementId, int amountInOre, DateTime dueDate,
            string description, string idempotencyKey, bool? isTest = null);

        Task<VippsCreatePaymentResponse> CreatePaymentAsync(
            Order order, List<VippsOrderLine> receiptLines, string redirectUrl,
            string phoneNumber, string idempotencyKey);
        Task<VippsPayment> GetPaymentAsync(string reference, bool? isTest = null);
        Task<VippsUserInfo?> GetUserInfoAsync(string sub, bool? isTest = null);
        Task<VippsPayment> CapturePaymentAsync(string reference, int amountInOre, string idempotencyKey, bool? isTest = null);
        Task CancelPaymentAsync(string reference, string idempotencyKey, bool? isTest = null);
        Task<VippsPayment> RefundPaymentAsync(string reference, int amountInOre, string idempotencyKey, bool? isTest = null);
        /// <summary>Refunds part or all of a captured recurring charge. Vipps sends no webhook for it.</summary>
        Task RefundChargeAsync(string agreementId, string chargeId, int amountInOre, string description, string idempotencyKey, bool? isTest = null);
    }
}
