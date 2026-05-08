using System.ComponentModel.DataAnnotations.Schema;
using System.ComponentModel.DataAnnotations;

public class UserProfile
{
    [Key, ForeignKey("User")]
    public required string Id { get; set; }
    public required User User { get; set; }

    [MaxLength(10)]
    public string PriceZone { get; set; } = "NO2";

    public bool PushNotificationsEnabled { get; set; } = false;

    public int OfflineAlertThresholdHours { get; set; } = 4;
}
