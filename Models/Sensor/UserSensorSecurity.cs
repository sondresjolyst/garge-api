namespace garge_api.Models.Sensor
{
    public class UserSensorSecurity
    {
        public string UserId { get; set; } = default!;
        public int SensorId { get; set; }
        public bool Enabled { get; set; }
        public int ThresholdMinutes { get; set; } = Constants.SecurityMode.DefaultThresholdMinutes;
        public DateTime? EnabledAt { get; set; }
        public int? EnforcingAutomationRuleId { get; set; }
        public DateTime CreatedAt { get; set; }
        public User User { get; set; } = default!;
        public Sensor Sensor { get; set; } = default!;
    }
}
