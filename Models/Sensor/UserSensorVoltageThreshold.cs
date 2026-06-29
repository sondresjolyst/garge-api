namespace garge_api.Models.Sensor
{
    public class UserSensorVoltageThreshold
    {
        public string UserId { get; set; } = default!;
        public int SensorId { get; set; }
        public double WarningVoltage { get; set; }
        public double CriticalVoltage { get; set; }
        public DateTime CreatedAt { get; set; }
        public User User { get; set; } = default!;
        public Sensor Sensor { get; set; } = default!;
    }
}
