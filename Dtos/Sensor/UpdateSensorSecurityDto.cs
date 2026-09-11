namespace garge_api.Dtos.Sensor
{
    public class UpdateSensorSecurityDto
    {
        public required bool Enabled { get; set; }
        public int? ThresholdMinutes { get; set; }
    }
}
