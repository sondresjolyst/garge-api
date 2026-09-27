using System.ComponentModel.DataAnnotations;
using garge_api.Constants;

namespace garge_api.Dtos.Admin
{
    public class SecuritySettingsDto
    {
        public int AlertThresholdMinutes { get; set; }
        public int MinAlertThresholdMinutes { get; set; } = SecurityMode.MinThresholdMinutes;
        public int MaxAlertThresholdMinutes { get; set; } = SecurityMode.MaxThresholdMinutes;
        public int WakeIntervalSeconds { get; set; } = SecurityMode.ShortSleepSeconds;
    }

    public class UpdateSecuritySettingsDto
    {
        [Range(SecurityMode.MinThresholdMinutes, SecurityMode.MaxThresholdMinutes)]
        public int AlertThresholdMinutes { get; set; }
    }
}
