using System.ComponentModel.DataAnnotations;

namespace garge_api.Dtos.Auth
{
    public class RequestPasswordResetDto
    {
        [Required]
        public required string Email { get; set; }
    }
}
