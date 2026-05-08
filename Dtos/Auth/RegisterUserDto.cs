using System.ComponentModel.DataAnnotations;

namespace garge_api.Dtos.Auth
{
    public class RegisterUserDto
    {
        [Required]
        [StringLength(64, MinimumLength = 3)]
        public required string UserName { get; set; }

        [Required]
        [EmailAddress]
        [StringLength(254)]
        public required string Email { get; set; }

        [Required]
        [StringLength(128, MinimumLength = 10)]
        public required string Password { get; set; }

        [Required]
        [StringLength(50, MinimumLength = 1)]
        public required string FirstName { get; set; }

        [Required]
        [StringLength(50, MinimumLength = 1)]
        public required string LastName { get; set; }
    }
}
