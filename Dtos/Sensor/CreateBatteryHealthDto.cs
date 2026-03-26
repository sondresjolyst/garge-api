using System.ComponentModel.DataAnnotations;

namespace garge_api.Dtos.Sensor
{
    public class CreateBatteryHealthDto
    {
        [Required]
        public required string Status { get; set; }
        public float Baseline { get; set; }
        public float LastCharge { get; set; }
        public float DropPct { get; set; }
        public int ChargesRecorded { get; set; }
    }
}
