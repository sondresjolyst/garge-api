namespace garge_api.Constants
{
    public static class RoleNames
    {
        // Canonical role names. These are the values seeded into Identity and issued in JWTs,
        // so [Authorize(Roles = ...)] and IsInAnyRole(...) checks must use these exact strings.
        public const string Admin = "Admin";
        public const string SensorAdmin = "SensorAdmin";
        public const string SwitchAdmin = "SwitchAdmin";
        public const string MqttAdmin = "MqttAdmin";
        public const string AutomationAdmin = "AutomationAdmin";

        public static readonly string[] AllRoles =
        {
            "Default", "Electricity",
            "Admin", "SensorAdmin", "MqttAdmin", "AutomationAdmin", "SwitchAdmin",
            "ComplimentaryUser", "DeviceBridge"
        };

        public static readonly string[] SubscriptionBypassRoles =
        {
            "Admin", "SensorAdmin", "MqttAdmin", "AutomationAdmin", "SwitchAdmin",
            "ComplimentaryUser", "DeviceBridge"
        };

        public static readonly Dictionary<string, string[]> RolePermissions = new()
        {
            { "Default", new string[] { "Electricity" } },
        };

        public static readonly HashSet<string> KnownPermissions =
            RolePermissions.Values.SelectMany(p => p).ToHashSet(StringComparer.Ordinal);
    }
}
