using System.ComponentModel.DataAnnotations;

namespace garge_api.Models.Admin
{
    public class AppSettings
    {
        public int Id { get; set; } = 1;
        public bool CookieBannerEnabled { get; set; } = true;
        public bool VatEnabled { get; set; } = false;
        public string? VippsShopWebhookId { get; set; }
        public string? VippsShopWebhookSecret { get; set; }
        public string? VippsSubscriptionWebhookId { get; set; }
        public string? VippsSubscriptionWebhookSecret { get; set; }
        // Vipps' test environment is separate from production and needs its own registrations.
        public string? VippsTestShopWebhookId { get; set; }
        public string? VippsTestShopWebhookSecret { get; set; }
        public string? VippsTestSubscriptionWebhookId { get; set; }
        public string? VippsTestSubscriptionWebhookSecret { get; set; }

        [Range(Constants.SecurityMode.MinThresholdMinutes, Constants.SecurityMode.MaxThresholdMinutes)]
        public int SecurityAlertThresholdMinutes { get; set; } = Constants.SecurityMode.DefaultThresholdMinutes;

        [MaxLength(100)]
        public string CompanyName { get; set; } = "Garge";
        [MaxLength(200)]
        public string CompanyLegalName { get; set; } = "Sjølyst Innovation AS";
        [MaxLength(20)]
        public string CompanyOrgNumber { get; set; } = "938 517 789";
        [MaxLength(500)]
        public string CompanyAddress { get; set; } = "Mårvegen 21a, 4347 Lye";
        [MaxLength(200)]
        public string CompanyEmail { get; set; } = "sondresjoelyst@gmail.com";

        public bool VippsTestMode { get; set; }
    }
}
