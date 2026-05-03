using System.ComponentModel.DataAnnotations;

namespace garge_api.Dtos.User
{
    public class UpdateUserPreferencesDto
    {
        [Required]
        [MaxLength(10)]
        public required string PriceZone { get; set; }

        public bool? PushNotificationsEnabled { get; set; }

        [Range(1, 168)]
        public int? OfflineAlertThresholdHours { get; set; }
    }
}
