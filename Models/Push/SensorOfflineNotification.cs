using System.ComponentModel.DataAnnotations;

namespace garge_api.Models.Push
{
    public class SensorOfflineNotification
    {
        public int Id { get; set; }

        [Required]
        public required string UserId { get; set; }

        public int SensorId { get; set; }

        public DateTime NotifiedAt { get; set; } = DateTime.UtcNow;

        public DateTime? ResolvedAt { get; set; }
    }
}
