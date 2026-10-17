namespace garge_api.Dtos.Admin
{
    /// <summary>
    /// The settings only garge has. The company details are served by the site settings package at
    /// <c>/api/company</c> and <c>/api/settings</c>.
    /// </summary>
    public class AppSettingsDto
    {
        public bool CookieBannerEnabled { get; set; }
        public bool VatEnabled { get; set; }
        public DateTime? VatRegisteredFrom { get; set; }
        public bool VippsTestMode { get; set; }
    }
}
