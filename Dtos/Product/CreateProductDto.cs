using System.ComponentModel.DataAnnotations;

namespace garge_api.Dtos.Product
{
    public class CreateProductDto
    {
        [Required]
        [MaxLength(50)]
        public required string Name { get; set; }

        [MaxLength(250)]
        public string? Description { get; set; }

        [Required]
        public decimal Price { get; set; }

        [MaxLength(3)]
        public string? Currency { get; set; }

        [Required]
        public int Stock { get; set; }

        [MaxLength(50)]
        public string? Category { get; set; }

        [MaxLength(50)]
        public string? Manufacturer { get; set; }
    }
}
