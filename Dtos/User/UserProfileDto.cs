using System.ComponentModel.DataAnnotations;

namespace garge_api.Dtos.User
{
    public class UserProfileDto
    {
        [Required]
        public required string Id { get; set; }

        public string? FirstName { get; set; }
        public string? LastName { get; set; }
        public string? Email { get; set; }
        public string? PhoneNumber { get; set; }
        public bool EmailConfirmed { get; set; }
        public string PriceZone { get; set; } = "NO2";
        public bool PushNotificationsEnabled { get; set; }
        public int OfflineAlertThresholdHours { get; set; } = 4;
    }
}
