namespace garge_api.Services
{
    public class VippsOptions
    {
        public required string ClientId { get; set; }
        public required string ClientSecret { get; set; }
        public required string MerchantSerialNumber { get; set; }
        public required string SubscriptionKey { get; set; }
        public required string BaseUrl { get; set; }
    }
}
