using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace garge_api.Models.Switch
{
    public class UserSwitch
    {
        [Required]
        public required string UserId { get; set; }

        [Required]
        public int SwitchId { get; set; }

        public bool IsOwner { get; set; } = true;

        /// <summary>
        /// For a shared row (<see cref="IsOwner"/> = false), what the recipient may do. Ignored for the
        /// owner's own row. Defaults to <see cref="SharePermission.Read"/>.
        /// </summary>
        public SharePermission Permission { get; set; } = SharePermission.Read;

        /// <summary>
        /// When set, the owner has turned this switch off (or it was auto-suspended for being over
        /// quota). Reads are blocked while suspended, but telemetry keeps flowing. Null = active.
        /// </summary>
        public DateTime? SuspendedAt { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [ForeignKey(nameof(UserId))]
        public User? User { get; set; }

        [ForeignKey(nameof(SwitchId))]
        public Switch? Switch { get; set; }
    }
}
