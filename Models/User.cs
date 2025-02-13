using System.ComponentModel.DataAnnotations;

namespace garge_api.Models
{
    public class User
    {
        [Key]
        public int Id { get; set; }
        [MaxLength(50)]
        public string Username { get; set; }
        [Required]
        [MaxLength(50)]
        public required string Email { get; set; }
        [Required]
        public required string Password { get; set; }
    }

    public class LoginModel
    {
        [Required]
        [MaxLength(50)]
        public required string Email { get; set; }
        [Required]
        public required string Password { get; set; }
    }
}
