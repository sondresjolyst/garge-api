namespace garge_api.Dtos.Sensor
{
    public class SensorSecuritySummaryDto
    {
        public bool Enabled { get; set; }
        public required string State { get; set; }
    }
}
