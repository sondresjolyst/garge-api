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

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [ForeignKey(nameof(UserId))]
        public User? User { get; set; }

        [ForeignKey(nameof(SwitchId))]
        public Switch? Switch { get; set; }
    }
}
