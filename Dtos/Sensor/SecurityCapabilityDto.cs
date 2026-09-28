namespace garge_api.Dtos.Sensor
{
    public class SecurityCapabilityDto
    {
        /// <summary>True when the device's config carries Garge Security settings.</summary>
        public required bool Capable { get; set; }
    }
}
