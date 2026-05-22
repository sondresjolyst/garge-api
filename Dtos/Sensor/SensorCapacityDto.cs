namespace garge_api.Dtos.Sensor
{
    /// <summary>The caller's sensor capacity and whether they may claim another sensor.</summary>
    public sealed class SensorCapacityDto
    {
        /// <summary>Sensors covered by the active subscription (1 Primary + add-on quantities); 0 without one.</summary>
        public int Capacity { get; set; }

        /// <summary>Active (non-suspended) owned sensors currently consuming capacity.</summary>
        public int Used { get; set; }

        /// <summary>True when a role grants service access without a subscription (e.g. ComplimentaryUser) — no capacity limit.</summary>
        public bool Bypass { get; set; }

        /// <summary>True when the caller may claim another sensor.</summary>
        public bool CanClaim { get; set; }
    }
}
