using System.ComponentModel.DataAnnotations;

namespace garge_api.Models.Admin
{
    public class AppSettings
    {
        public int Id { get; set; } = 1;
        public bool CookieBannerEnabled { get; set; } = true;
        public bool VatEnabled { get; set; } = false;

        /// <summary>
        /// The first moment sales carry VAT, which is the registration date. Set when VAT is turned on
        /// and cleared when it is turned off. Without a date, VAT on covers every sale.
        /// </summary>
        public DateTime? VatRegisteredFrom { get; set; }

        /// <summary>Whether a sale made at <paramref name="saleAt"/> carries VAT.</summary>
        public bool VatOnAt(DateTime saleAt) => VatEnabled && (VatRegisteredFrom is not { } from || saleAt >= from);
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

        /// <summary>Sales outside garge-api in the last 12 months, in øre, counted toward the VAT threshold.</summary>
        [Range(0, long.MaxValue)]
        public long OtherTurnoverInOre { get; set; }

        /// <summary>The highest VAT threshold warning sent to the admins: 0, 80, 90, or 100 once passed.</summary>
        public int VatThresholdWarnedPercent { get; set; }

        /// <summary>The sale that passed the VAT threshold, once found. VAT is owed from it on until VAT is on.</summary>
        public int? VatCrossingInvoiceId { get; set; }
        public DateTime? VatCrossedAt { get; set; }
    }
}
