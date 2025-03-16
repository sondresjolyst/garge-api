namespace garge_api.Constants
{
    public static class RoleNames
    {
        public static readonly string[] AllRoles = { "Default", "Electricity" };
        public static readonly Dictionary<string, string[]> RolePermissions = new Dictionary<string, string[]>
        {
            { "Default", new string[] { "Electricity" } },
        };
    }
}
