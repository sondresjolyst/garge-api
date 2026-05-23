using garge_api.Models;

namespace garge_api.Dtos.Sensor
{
    /// <summary>Request to share a sensor with another Garge user (by their account email).</summary>
    public class ShareSensorDto
    {
        public required string Email { get; set; }

        /// <summary>Read (view only) or Edit (control switches, automations, calibration).</summary>
        public SharePermission Permission { get; set; } = SharePermission.Read;
    }

    /// <summary>A single recipient of a shared sensor, returned to the owner.</summary>
    public class SensorShareDto
    {
        public required string UserId { get; set; }
        public required string Email { get; set; }
        public required string FirstName { get; set; }
        public required string LastName { get; set; }
        public SharePermission Permission { get; set; }
        public DateTime SharedAt { get; set; }
    }
}
