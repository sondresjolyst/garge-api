using System.ComponentModel.DataAnnotations;

namespace garge_api.Dtos.User
{
    public class UserProfileDto
    {
        [Required]
        public required string Id { get; set; }

        [Required]
        public required string FirstName { get; set; }

        [Required]
        public required string LastName { get; set; }

        [Required]
        public required string Email { get; set; }
        public bool EmailConfirmed { get; set; }
    }
}
