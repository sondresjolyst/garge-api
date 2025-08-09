namespace garge_api.Dtos.Sensor
{
    public class SensorDto
    {
        public int Id { get; set; }
        public required string Name { get; set; }
        public required string Type { get; set; }
        public required string Role { get; set; }
        public required string RegistrationCode { get; set; }
        public string? CustomName { get; set; }
        public required string DefaultName { get; set; }
        public required string ParentName { get; set; }
    }
}
