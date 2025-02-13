using System.ComponentModel.DataAnnotations;

namespace garge_api.Models
{
    public class Customer
    {
        [Key]
        public int CustomerId { get; set; }
        [Required]
        [MaxLength(50)]
        public required string FirstName { get; set; }
        [Required]
        [MaxLength(50)]
        public required string LastName { get; set; }
        [Required]
        [MaxLength(100)]
        public required string Email { get; set; }
    }
}