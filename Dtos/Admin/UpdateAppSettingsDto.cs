using System.ComponentModel.DataAnnotations;

namespace garge_api.Dtos.Admin
{
    public class UpdateAppSettingsDto
    {
        public bool CookieBannerEnabled { get; set; }
        public bool VatEnabled { get; set; }
        [MaxLength(100)] public string CompanyName { get; set; } = string.Empty;
        [MaxLength(200)] public string CompanyLegalName { get; set; } = string.Empty;
        [MaxLength(20)]  public string CompanyOrgNumber { get; set; } = string.Empty;
        [MaxLength(500)] public string CompanyAddress { get; set; } = string.Empty;
        [MaxLength(200)] public string CompanyEmail { get; set; } = string.Empty;
    }
}
