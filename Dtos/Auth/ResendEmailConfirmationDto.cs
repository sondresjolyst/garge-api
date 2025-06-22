using System.ComponentModel.DataAnnotations;

namespace garge_api.Dtos.Auth
{
    public class ResendEmailConfirmationDto
    {
        [Required]
        public required string Email { get; set; }
    }
}
