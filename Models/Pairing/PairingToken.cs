using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace garge_api.Models.Pairing
{
    /// <summary>
    /// A short-lived, single-use code a logged-in user mints in the app to pair a physical device.
    /// The device sends the token over MQTT during setup and the operator service calls back into
    /// this API (<c>POST /api/pairing/claim</c>) to claim the device's sensors and switches for the
    /// minting user. Consumed tokens are kept (with <see cref="ConsumedAt"/> set) as an audit trail.
    /// </summary>
    public class PairingToken
    {
        [Key]
        public int Id { get; set; }

        [Required]
        [MaxLength(12)]
        public required string Token { get; set; }

        [Required]
        public required string UserId { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime ExpiresAt { get; set; }

        /// <summary>When the operator redeemed this token, or null while still unused.</summary>
        public DateTime? ConsumedAt { get; set; }

        /// <summary>The parent device name the token was redeemed for, for auditing.</summary>
        [MaxLength(100)]
        public string? ConsumedByParentName { get; set; }

        [ForeignKey(nameof(UserId))]
        public User? User { get; set; }
    }
}
