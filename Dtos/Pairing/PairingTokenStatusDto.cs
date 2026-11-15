namespace garge_api.Dtos.Pairing
{
    public class PairingTokenStatusDto
    {
        public const string Waiting = "waiting";
        public const string Provisioned = "provisioned";
        public const string Paired = "paired";
        public const string Expired = "expired";
        public const string OwnedByOther = "ownedByOther";

        /// <summary>
        /// One of <c>waiting</c>, <c>provisioned</c>, <c>paired</c>, <c>expired</c> or
        /// <c>ownedByOther</c>, the last when provisioning was refused because another user owns the device.
        /// </summary>
        public required string Status { get; set; }

        /// <summary>
        /// Sensors under the paired device that the caller owns. Only filled when paired.
        /// Empty on a paired token when another user owns the device.
        /// </summary>
        public List<int> SensorIds { get; set; } = new();

        /// <summary>Switches discovered by the paired device that the caller owns. Only filled when paired.</summary>
        public List<int> SwitchIds { get; set; } = new();
    }
}
