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

        public bool IsOwner { get; set; } = true;

        /// <summary>
        /// When set, the owner has turned this sensor off (or it was auto-suspended for being over
        /// quota). Suspended owned sensors do not count toward subscription capacity and their
        /// dashboard/history reads are blocked, but telemetry keeps flowing. Null = active.
        /// </summary>
        public DateTime? SuspendedAt { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [ForeignKey(nameof(UserId))]
        public User? User { get; set; }

        [ForeignKey(nameof(SensorId))]
        public Sensor? Sensor { get; set; }
    }
}
