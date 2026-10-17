using garge_api.Models;
using garge_api.Models.Admin;
using SjolystInnovation.AppKit.Auth;

namespace garge_api.Services
{
    /// <summary>Gives the auth emails the company details from the admin settings, as the order emails show them.</summary>
    public class AuthEmailBrand(ApplicationDbContext db) : IEmailBrand
    {
        public async Task<EmailBrand> GetBrandAsync(CancellationToken cancellationToken)
        {
            var s = await db.AppSettings.FindAsync([1], cancellationToken) ?? new AppSettings();
            return new EmailBrand(s.CompanyName)
            {
                LegalName = s.CompanyLegalName,
                OrgNumber = s.OrgNumber,
                VatRegistered = s.VatEnabled,
                Address = s.FormattedAddress,
                Email = s.PublicEmail,
            };
        }
    }
}
