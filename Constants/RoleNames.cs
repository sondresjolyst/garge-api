namespace garge_api.Constants
{
    public static class RoleNames
    {
        public static readonly string[] AllRoles = { "Default", "Electricity", "Admin", "SensorAdmin", "MqttAdmin", "AutomationAdmin", "SwitchAdmin" };

        // Roles that bypass the subscription requirement — these accounts manage infrastructure, not end-user features
        public static readonly string[] SubscriptionBypassRoles = { "Admin", "SensorAdmin", "MqttAdmin", "AutomationAdmin", "SwitchAdmin" };
        public static readonly Dictionary<string, string[]> RolePermissions = new Dictionary<string, string[]>
        {
            { "Default", new string[] { "Electricity" } },
        };
    }
}
