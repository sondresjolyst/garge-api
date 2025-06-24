using System.ComponentModel.DataAnnotations;

namespace garge_api.Dtos.Auth
{
    public class ResetPasswordDto
    {
        [Required]
        public required string Email { get; set; }
        [Required]
        public required string Code { get; set; }
        [Required]
        public required string NewPassword { get; set; }
    }
}
