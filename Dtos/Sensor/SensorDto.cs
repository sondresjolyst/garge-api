using garge_api.Models;

namespace garge_api.Dtos.Sensor
{
    public class SensorDto
    {
        public int Id { get; set; }
        public required string Name { get; set; }
        public required string Type { get; set; }
        public required string Role { get; set; }
        public required string RegistrationCode { get; set; }
        public string? CustomName { get; set; }
        public required string DefaultName { get; set; }
        public required string ParentName { get; set; }

        /// <summary>
        /// The caller's voltage color thresholds for this sensor. Both are null when unset, in which
        /// case the client leaves the reading uncolored. A reading at or above <see cref="WarningVoltage"/>
        /// is normal, below it is a warning, and below <see cref="CriticalVoltage"/> is critical.
        /// </summary>
        public double? WarningVoltage { get; set; }
        public double? CriticalVoltage { get; set; }

        /// <summary>True when the caller has this owned sensor turned off / over-quota suspended. Data reads are blocked while suspended.</summary>
        public bool Suspended { get; set; }

        /// <summary>
        /// The caller's relationship to this sensor: <c>owner</c> (or admin), <c>edit</c> (Edit share),
        /// or <c>read</c> (Read share). Drives which controls the client shows.
        /// </summary>
        public string Access { get; set; } = DeviceAccess.Owner;
    }
}
