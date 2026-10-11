namespace garge_api.Services
{
    public interface ISubscriptionEmailService
    {
        Task<bool> SendActivatedAsync(int subscriptionId);
        Task<bool> SendChargeFailedAsync(int subscriptionId);
        Task<bool> SendStoppedForNonPaymentAsync(int subscriptionId);

        /// <summary>
        /// Tells the subscriber of a price increase, and of their right to cancel at no cost.
        /// Returns whether the email was sent, as the notice counts only once it is.
        /// </summary>
        Task<bool> SendPriceChangeNoticeAsync(int subscriptionId, int oldUnitPriceInOre, int newUnitPriceInOre, DateTime effectiveFrom);
    }
}
