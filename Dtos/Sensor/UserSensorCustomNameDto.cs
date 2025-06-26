using System.ComponentModel.DataAnnotations;

namespace garge_api.Dtos.Sensor
{
    public class UserSensorCustomNameDto
    {
        public required string UserId { get; set; }
        public required int SensorId { get; set; }
        public required string CustomName { get; set; }
        public required DateTime CreatedAt { get; set; }
    }
}
