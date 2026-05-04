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

        [MaxLength(100)]
        public string CompanyName { get; set; } = "Garge";
        [MaxLength(200)]
        public string CompanyLegalName { get; set; } = "Sjølyst Innovations";
        [MaxLength(20)]
        public string CompanyOrgNumber { get; set; } = "934 531 035";
        [MaxLength(500)]
        public string CompanyAddress { get; set; } = "Mårvegen 21a, 4347 Lye";
        [MaxLength(200)]
        public string CompanyEmail { get; set; } = "sondresjoelyst@gmail.com";
    }
}
