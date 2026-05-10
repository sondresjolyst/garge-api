using System.ComponentModel.DataAnnotations;

namespace garge_api.Dtos.Auth
{
    public class ResetPasswordDto
    {
        [Required]
        [EmailAddress]
        [StringLength(254)]
        public required string Email { get; set; }

        [Required]
        [StringLength(128, MinimumLength = 6)]
        public required string Code { get; set; }

        [Required]
        [StringLength(128, MinimumLength = 10)]
        public required string NewPassword { get; set; }
    }
}
