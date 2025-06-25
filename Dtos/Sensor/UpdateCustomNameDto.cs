using System.ComponentModel.DataAnnotations;

namespace garge_api.Dtos.Sensor
{
    public class UpdateCustomNameDto
    {
        [MaxLength(50)]
        public required string CustomName { get; set; }

    }
}
