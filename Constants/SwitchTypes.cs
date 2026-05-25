namespace garge_api.Constants
{
    /// <summary>
    /// Canonical switch device-type values. Mirrors <see cref="SensorTypes"/>. The API uses
    /// "switch" as the device class; the only concrete type today is the socket.
    /// </summary>
    public static class SwitchTypes
    {
        public const string Socket = "socket";

        public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Socket,
        };

        public static bool IsAllowed(string? type) =>
            !string.IsNullOrWhiteSpace(type) && Allowed.Contains(type);
    }
}
