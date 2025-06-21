using System.ComponentModel.DataAnnotations;

namespace garge_api.Dtos.Switch
{
    public class CreateSwitchDto
    {
        [Required]
        [MaxLength(50)]
        public string Name { get; set; } = string.Empty;

        [Required]
        [MaxLength(50)]
        public string Type { get; set; } = string.Empty;
    }
}
