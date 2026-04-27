namespace garge_api.Dtos.Sensor
{
    public class SensorActivityDto
    {
        public int Id { get; set; }
        public int SensorId { get; set; }
        public string UserId { get; set; } = default!;
        public required string Title { get; set; }
        public string? Notes { get; set; }
        public int? OdometerKm { get; set; }
        public DateTime ActivityDate { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
    }
}
