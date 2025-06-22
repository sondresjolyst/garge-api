namespace garge_api.Dtos.Sensor
{
    public class SensorDto
    {
        public int Id { get; set; }
        public required string Name { get; set; }
        public required string Type { get; set; }
        public required string Role { get; set; }
    }
}
