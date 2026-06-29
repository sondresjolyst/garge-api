using System.ComponentModel.DataAnnotations;

namespace garge_api.Dtos.Sensor
{
    public class UpdateVoltageThresholdDto
    {
        [Range(0, 100)]
        public required double WarningVoltage { get; set; }

        [Range(0, 100)]
        public required double CriticalVoltage { get; set; }
    }
}
