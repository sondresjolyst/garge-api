using System.ComponentModel.DataAnnotations;

namespace garge_api.Dtos.User
{
    public class UpdateProfileDto
    {
        [Required]
        [StringLength(50, MinimumLength = 1)]
        public required string FirstName { get; set; }

        [Required]
        [StringLength(50, MinimumLength = 1)]
        public required string LastName { get; set; }

        [Phone]
        [StringLength(20)]
        public string? PhoneNumber { get; set; }
    }
}
