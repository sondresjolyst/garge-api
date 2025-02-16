using System.ComponentModel.DataAnnotations;

namespace garge_api.Models
{
    public class RegisterModel
    {
        [Required]
        [MaxLength(50)]
        public required string UserName { get; set; }

        [Required]
        [MaxLength(50)]
        public required string FirstName { get; set; }

        [Required]
        [MaxLength(50)]
        public required string LastName { get; set; }

        [Required]
        [MaxLength(50)]
        public required string Email { get; set; }

        [Required]
        public required string Password { get; set; }
    }
}
