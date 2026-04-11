using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace garge_api.Models.Sensor
{
    public class UserSensor
    {
        [Required]
        public required string UserId { get; set; }

        [Required]
        public int SensorId { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [ForeignKey(nameof(UserId))]
        public User? User { get; set; }

        [ForeignKey(nameof(SensorId))]
        public Sensor? Sensor { get; set; }
    }
}
