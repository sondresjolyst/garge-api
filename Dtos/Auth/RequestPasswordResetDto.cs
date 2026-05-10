using System.ComponentModel.DataAnnotations;

namespace garge_api.Dtos.Auth
{
    public class RequestPasswordResetDto
    {
        [Required]
        [EmailAddress]
        [StringLength(254)]
        public required string Email { get; set; }
    }
}
