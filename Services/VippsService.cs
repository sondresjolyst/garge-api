using garge_api.Models.Shop;
using garge_api.Models.Subscription;
using Microsoft.Extensions.Options;

namespace garge_api.Services
{
    /// <summary>Turns garge's products and orders into Vipps requests, sent through <see cref="IVippsClient"/>.</summary>
    public class VippsService(IVippsClient client, IOptions<AppOptions> appOpts) : IVippsService
    {
        public Task<VippsCreateAgreementResponse> CreateAgreementAsync(
            Product product, string userId, string redirectUrl, string phoneNumber,
            int unitPriceInOre, int quantity, string idempotencyKey)
        {
            // VARIABLE pricing: the user approves a ceiling equal to the current unit price times the
            // quantity, and each charge may be at most that. Raising the quantity later raises the
            // ceiling, which Vipps asks the user to approve. Lowering it is a database change only.
            var ceiling = CheckedAgreementAmount((long)unitPriceInOre * quantity);
            return client.CreateAgreementAsync(new VippsCreateAgreementRequest
            {
                ProductName = product.Name,
                // Descriptions are markdown of up to 2000 characters. The client cuts them plainly.
                ProductDescription = product.Description,
                SuggestedMaxAmountInOre = ceiling,
                InitialChargeInOre = ceiling,
                InitialChargeDescription = product.Name,
                Interval = product.Interval == BillingInterval.Monthly ? VippsInterval.Month : VippsInterval.Year,
                MerchantRedirectUrl = redirectUrl,
                MerchantAgreementUrl = $"{appOpts.Value.FrontendBaseUrl}/terms",
                PhoneNumber = phoneNumber,
            }, idempotencyKey);
        }

        public Task<VippsAgreement> GetAgreementAsync(string agreementId, bool? isTest = null) =>
            client.GetAgreementAsync(agreementId, isTest);

        public Task CancelAgreementAsync(string agreementId, string idempotencyKey, bool? isTest = null) =>
            client.StopAgreementAsync(agreementId, idempotencyKey, isTest);

        public Task UpdateAgreementMaxAmountAsync(string agreementId, int newMaxAmountInOre, string idempotencyKey, bool? isTest = null) =>
            client.UpdateAgreementMaxAmountAsync(agreementId, newMaxAmountInOre, idempotencyKey, isTest);

        public Task<VippsCreateChargeResponse> CreateChargeAsync(
            string agreementId, int amountInOre, DateTime dueDate,
            string description, string idempotencyKey, bool? isTest = null) =>
            // The idempotency key doubles as the charge's order id, which Vipps makes its charge id.
            client.CreateChargeAsync(agreementId, new VippsCreateChargeRequest
            {
                AmountInOre = amountInOre,
                Due = DateOnly.FromDateTime(dueDate),
                Description = description,
                OrderId = idempotencyKey,
            }, idempotencyKey, isTest);

        public Task<VippsCreatePaymentResponse> CreatePaymentAsync(
            Order order, List<VippsOrderLine> receiptLines, string redirectUrl,
            string phoneNumber, string idempotencyKey) =>
            client.CreatePaymentAsync(new VippsCreatePaymentRequest
            {
                Reference = $"garge-order-{order.Id:D6}",
                AmountInOre = order.TotalInOre,
                PhoneNumber = phoneNumber,
                ReturnUrl = $"{redirectUrl}?orderId={order.Id}",
                Description = $"Garge order #{order.Id}",
                ReceiptLines = receiptLines.Select(l => new VippsReceiptLine
                {
                    Id = l.Id,
                    Name = l.Name,
                    UnitPriceInOre = l.UnitPriceInOre,
                    Quantity = l.Quantity,
                    TaxRateBasisPoints = l.TaxPercentageBasisPoints,
                }).ToList(),
            }, idempotencyKey);

        public Task<VippsPayment> GetPaymentAsync(string reference, bool? isTest = null) =>
            client.GetPaymentAsync(reference, isTest);

        public Task<VippsUserInfo?> GetUserInfoAsync(string sub, bool? isTest = null) =>
            client.GetUserInfoAsync(sub, isTest);

        public Task<VippsPayment> CapturePaymentAsync(string reference, int amountInOre, string idempotencyKey, bool? isTest = null) =>
            client.CapturePaymentAsync(reference, amountInOre, idempotencyKey, isTest);

        public Task CancelPaymentAsync(string reference, string idempotencyKey, bool? isTest = null) =>
            client.CancelPaymentAsync(reference, idempotencyKey, isTest);

        public Task<VippsPayment> RefundPaymentAsync(string reference, int amountInOre, string idempotencyKey, bool? isTest = null) =>
            client.RefundPaymentAsync(reference, amountInOre, idempotencyKey, isTest);

        public Task RefundChargeAsync(string agreementId, string chargeId, int amountInOre, string description, string idempotencyKey, bool? isTest = null) =>
            client.RefundChargeAsync(agreementId, chargeId, amountInOre, description, idempotencyKey, isTest);

        private static int CheckedAgreementAmount(long amountInOre) =>
            amountInOre is > 0 and <= SubscriptionCharges.MaxAgreementAmountInOre
                ? (int)amountInOre
                : throw new ArgumentOutOfRangeException(nameof(amountInOre), amountInOre,
                    $"A Vipps agreement amount must be between 1 and {SubscriptionCharges.MaxAgreementAmountInOre} øre.");
    }
}
