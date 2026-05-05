namespace garge_api.Services
{
    public class VippsOptions
    {
        public required string ClientId { get; set; }
        public required string ClientSecret { get; set; }
        public required string MerchantSerialNumber { get; set; }
        public required string SubscriptionKey { get; set; }
        public required string BaseUrl { get; set; }

        public string TestBaseUrl { get; set; } = "https://apitest.vipps.no";
        public string TestClientId { get; set; } = string.Empty;
        public string TestClientSecret { get; set; } = string.Empty;
        public string TestMerchantSerialNumber { get; set; } = string.Empty;
        public string TestSubscriptionKey { get; set; } = string.Empty;
    }
}
