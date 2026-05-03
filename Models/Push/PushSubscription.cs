using System.ComponentModel.DataAnnotations;
using garge_api.Models.Admin;

namespace garge_api.Models.Push
{
    public class PushSubscription
    {
        public int Id { get; set; }

        [Required]
        public required string UserId { get; set; }

        [Required]
        [MaxLength(2048)]
        public required string Endpoint { get; set; }

        [Required]
        [MaxLength(256)]
        public required string P256dh { get; set; }

        [Required]
        [MaxLength(64)]
        public required string Auth { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public User? User { get; set; }
    }
}
