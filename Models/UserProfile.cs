using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace garge_api.Models
{
    public class UserProfile
    {
        [Key, ForeignKey("User")]
        public required string Id { get; set; }

        [Required]
        [MaxLength(50)]
        public required string FirstName { get; set; }

        [Required]
        [MaxLength(50)]
        public required string LastName { get; set; }

        [Required]
        [MaxLength(50)]
        public required string Email { get; set; }

        public required ApplicationUser User { get; set; }
    }
}