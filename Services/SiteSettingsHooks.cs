using garge_api.Models.Admin;
using SjolystInnovation.AppKit.SiteSettings;

namespace garge_api.Services
{
    /// <summary>
    /// Garge's options for the site settings package. A company settings save through the package keeps
    /// the VAT registration date in step the same way <c>PUT /api/admin/settings</c> does, and every save
    /// clears the settings cache.
    /// </summary>
    public static class SiteSettingsHooks
    {
        public static void Configure(AppKitSiteSettingsOptions options)
        {
            options.Policy = "Admin";
            options.OnSaving = OnSaving;
            options.OnSaved = OnSaved;
        }

        /// <summary>The package sets <c>VatRegistered</c>, which is garge's <c>VatEnabled</c>. The date follows the switch.</summary>
        public static Task OnSaving(SiteSettingsSaving saving)
        {
            ((AppSettings)saving.Settings).SyncVatRegisteredFrom(DateTime.UtcNow);
            return Task.CompletedTask;
        }

        public static Task OnSaved(SiteSettingsSaved saved)
        {
            saved.Http.RequestServices.GetRequiredService<IAppSettingsCache>().Invalidate();
            return Task.CompletedTask;
        }
    }
}
