namespace garge_api.Dtos.Mqtt
{
    /// <summary>A state a target device should be put into, as published to its command topic.</summary>
    public class SetDeviceStateDto
    {
        public required string State { get; set; }
    }

    /// <summary>
    /// One command the operator still has to deliver: the wanted state, the last state the device
    /// was seen in, and the gateway currently allowed to act on it. A null controller means no
    /// gateway holds the lease, so the command cannot be delivered yet.
    /// </summary>
    public class PendingDeviceCommandDto
    {
        public required string Target { get; set; }
        public required string DesiredState { get; set; }
        public string? ObservedState { get; set; }
        public string? ControllerDeviceName { get; set; }
        public int Attempts { get; set; }
        public DateTime DesiredStateAt { get; set; }
    }

    /// <summary>
    /// The targets one gateway is allowed to act on. A gateway not holding a device's lease does
    /// not see it here, and so stays a standby for it.
    /// </summary>
    public class DeviceControlListDto
    {
        public required string GatewayDeviceName { get; set; }
        public required IReadOnlyList<string> Targets { get; set; }
    }
}
