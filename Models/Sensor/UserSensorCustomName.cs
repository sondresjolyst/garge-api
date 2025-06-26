namespace garge_api.Models.Sensor
{
    public class UserSensorCustomName
    {
        public string UserId { get; set; } = default!;
        public int SensorId { get; set; }
        public string CustomName { get; set; } = default!;
        public DateTime CreatedAt { get; set; }
        public User User { get; set; } = default!;
        public Sensor Sensor { get; set; } = default!;
    }
}
