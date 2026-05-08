namespace garge_api.Constants
{
    public static class RoleNames
    {
        public static readonly string[] AllRoles =
        {
            "Default", "Electricity",
            "Admin", "SensorAdmin", "MqttAdmin", "AutomationAdmin", "SwitchAdmin",
            "ComplimentaryUser"
        };

        public static readonly string[] SubscriptionBypassRoles =
        {
            "Admin", "SensorAdmin", "MqttAdmin", "AutomationAdmin", "SwitchAdmin",
            "ComplimentaryUser"
        };

        public static readonly Dictionary<string, string[]> RolePermissions = new()
        {
            { "Default", new string[] { "Electricity" } },
        };

        public static readonly HashSet<string> KnownPermissions =
            RolePermissions.Values.SelectMany(p => p).ToHashSet(StringComparer.Ordinal);
    }
}
