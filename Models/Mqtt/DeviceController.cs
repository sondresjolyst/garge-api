using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace garge_api.Models.Mqtt
{
    /// <summary>
    /// Which gateway may act on a shared target device. Several gateways can discover the same
    /// Wiz device, and every one of them would otherwise publish its state and answer its command
    /// topic, so one holds a lease and the rest stand by. The lease is renewed while the holder
    /// still sees the target, and lapses if it stops, which is what lets a standby take over when
    /// a gateway dies or moves out of range.
    /// </summary>
    [Index(nameof(Target), IsUnique = true)]
    public class DeviceController
    {
        [Key]
        public int Id { get; set; }

        /// <summary>The device being controlled, e.g. <c>wiz_SOCKET_6c2990a96cde</c>.</summary>
        [Required]
        [MaxLength(100)]
        public required string Target { get; set; }

        /// <summary>The gateway holding the lease, e.g. <c>garge_48ca43597fd8</c>.</summary>
        [Required]
        [MaxLength(100)]
        public required string ControllerDeviceName { get; set; }

        /// <summary>
        /// When the lease stops being valid. Pushed forward while the holder reports the target;
        /// once it is in the past any candidate may take over.
        /// </summary>
        public DateTime LeaseExpiresAt { get; set; }

        /// <summary>
        /// When the holder last reported seeing this target. A gateway can be online and
        /// publishing its own sensors while out of UDP range of the target, so gateway liveness
        /// alone is not enough to hold the lease.
        /// </summary>
        public DateTime LastSeenFromTarget { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
