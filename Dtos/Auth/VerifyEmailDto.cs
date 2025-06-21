using System.ComponentModel.DataAnnotations;

namespace garge_api.Dtos.Auth
{
    public class VerifyEmailDto
    {
        [Required]
        public required string Email { get; set; }

        [Required]
        public required string Code { get; set; }
    }
}
