using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SjolystInnovation.AppKit.SiteSettings;

namespace garge_api.Models.Admin
{
    public class AppSettings : ICompanySettings, IBrandingSettings
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

        /// <summary>
        /// Keeps the registration date in step with the switch when no date is given. Turning VAT on
        /// starts it at <paramref name="now"/> unless a date is already set, and turning it off clears the date.
        /// </summary>
        public void SyncVatRegisteredFrom(DateTime now)
        {
            if (VatEnabled)
                VatRegisteredFrom ??= now;
            else
                VatRegisteredFrom = null;
        }
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

        /// <summary>The address the contact form sends its messages to. Not shown publicly.</summary>
        [MaxLength(200)]
        public string ContactRecipientEmail { get; set; } = "sondresjoelyst@gmail.com";
        [MaxLength(100)]
        public string CompanyName { get; set; } = "Garge";
        [MaxLength(200)]
        public string CompanyLegalName { get; set; } = "Sjølyst Innovation AS";
        [MaxLength(20)]
        public string OrgNumber { get; set; } = "938 517 789";

        /// <summary>The package's name for <see cref="VatEnabled"/>, which stays the column garge reads.</summary>
        bool ICompanySettings.VatRegistered { get => VatEnabled; set => VatEnabled = value; }

        [MaxLength(200)]
        public string StreetAddress { get; set; } = "Mårvegen 21a";
        [MaxLength(20)]
        public string PostalCode { get; set; } = "4347";
        [MaxLength(100)]
        public string AddressLocality { get; set; } = "Lye";
        [MaxLength(100)]
        public string AddressRegion { get; set; } = "";

        /// <summary>The address as one line, as emails and invoices show it. Derived, never stored.</summary>
        [NotMapped]
        public string FormattedAddress => PostalAddress.Format(StreetAddress, PostalCode, AddressLocality);

        [MaxLength(200)]
        public string PublicEmail { get; set; } = "sondresjoelyst@gmail.com";

        /// <summary>Empty until an admin enters one. The package requires it before its settings save goes through.</summary>
        [MaxLength(40)]
        public string PublicPhone { get; set; } = "";

        public string? LogoData { get; set; }
        [MaxLength(50)]
        public string? LogoContentType { get; set; }
        public string? IconData { get; set; }
        [MaxLength(50)]
        public string? IconContentType { get; set; }

        public bool VippsTestMode { get; set; }

        /// <summary>The highest VAT threshold warning sent to the admins: 0, 80, 90, or 100 once passed.</summary>
        public int VatThresholdWarnedPercent { get; set; }

        /// <summary>
        /// When turnover passed the VAT threshold, once found, and the sale that passed it. VAT is owed from
        /// then on until VAT is on. The sale is empty when sales outside garge-api passed it, and the time is
        /// then the start of that day in Norway.
        /// </summary>
        public int? VatCrossingInvoiceId { get; set; }
        public DateTime? VatCrossedAt { get; set; }
    }
}
