namespace garge_api.Dtos.Sensor
{
    public class SensorDataDto
    {
        public int Id { get; set; }
        public int SensorId { get; set; }
        public required string Value { get; set; }
        public DateTime Timestamp { get; set; }
    }
}
