namespace garge_api.Dtos.Admin
{
    public class AppSettingsDto
    {
        public bool CookieBannerEnabled { get; set; }
        public bool VatEnabled { get; set; }
        public bool VippsTestMode { get; set; }
        public string CompanyName { get; set; } = string.Empty;
        public string CompanyLegalName { get; set; } = string.Empty;
        public string CompanyOrgNumber { get; set; } = string.Empty;
        public string CompanyAddress { get; set; } = string.Empty;
        public string CompanyEmail { get; set; } = string.Empty;
    }

    public class PublicSettingsDto
    {
        public bool CookieBannerEnabled { get; set; }
        public bool VatEnabled { get; set; }
        public bool VippsTestMode { get; set; }
        public string CompanyName { get; set; } = string.Empty;
        public string CompanyLegalName { get; set; } = string.Empty;
        public string CompanyOrgNumber { get; set; } = string.Empty;
        public string CompanyAddress { get; set; } = string.Empty;
        public string CompanyEmail { get; set; } = string.Empty;
    }
}
