using System.ComponentModel.DataAnnotations;

namespace garge_api.Dtos.Switch
{
    public class CreateSwitchDataDto
    {
        [Required]
        public string Value { get; set; } = string.Empty;
    }
}
