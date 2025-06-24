using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace garge_api.Models.Auth
{
    public class RefreshToken
    {
        [Key]
        public int Id { get; set; }
        [Required]
        public string Token { get; set; } = default!;
        [Required]
        public string UserId { get; set; } = default!;
        [ForeignKey("UserId")]
        public User User { get; set; } = default!;
        public DateTime Expires { get; set; }
        public DateTime Created { get; set; }
        public DateTime? Revoked { get; set; }
    }
}
