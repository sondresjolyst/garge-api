using System.ComponentModel.DataAnnotations;

namespace garge_api.Models.Auth
{
    public class LoginModel
    {
        [Required]
        [MaxLength(50)]
        public required string Email { get; set; }

        [Required]
        public required string Password { get; set; }
    }
}
