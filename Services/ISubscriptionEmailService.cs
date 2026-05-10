namespace garge_api.Services
{
    public interface ISubscriptionEmailService
    {
        Task SendActivatedAsync(int subscriptionId);
        Task SendChargeFailedAsync(int subscriptionId);
    }
}
