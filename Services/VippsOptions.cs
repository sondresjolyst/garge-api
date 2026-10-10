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

        /// <summary>Whether the credentials for an environment are all set.</summary>
        public bool IsConfigured(bool isTest) => isTest
            ? !string.IsNullOrEmpty(TestClientId) && !string.IsNullOrEmpty(TestClientSecret)
              && !string.IsNullOrEmpty(TestMerchantSerialNumber) && !string.IsNullOrEmpty(TestSubscriptionKey)
            : !string.IsNullOrEmpty(ClientId) && !string.IsNullOrEmpty(ClientSecret)
              && !string.IsNullOrEmpty(MerchantSerialNumber) && !string.IsNullOrEmpty(SubscriptionKey);
    }
}
