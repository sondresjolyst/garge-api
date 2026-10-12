using System.ComponentModel.DataAnnotations;

namespace garge_api.Dtos.Auth
{
    public class LogoutRequestDto
    {
        [Required]
        public required string RefreshToken { get; set; }
    }
}
