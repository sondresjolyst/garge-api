namespace garge_api.Dtos.Sensor
{
    public class UserSensorVoltageThresholdDto
    {
        public required string UserId { get; set; }
        public required int SensorId { get; set; }
        public required double WarningVoltage { get; set; }
        public required double CriticalVoltage { get; set; }
        public required DateTime CreatedAt { get; set; }
    }
}
