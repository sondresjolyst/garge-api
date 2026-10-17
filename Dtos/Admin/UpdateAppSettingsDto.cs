namespace garge_api.Dtos.Admin
{
    public class UpdateAppSettingsDto
    {
        public bool? CookieBannerEnabled { get; set; }
        public bool? VatEnabled { get; set; }
        /// <summary>The registration date. Setting it also turns VAT on.</summary>
        public DateTime? VatRegisteredFrom { get; set; }
        public bool? VippsTestMode { get; set; }
    }
}
