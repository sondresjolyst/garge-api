namespace garge_api.Constants
{
    /// <summary>
    /// Canonical on/off state values a switch (socket) can be set to, plus the set of valid
    /// states accepted by the switch-data write endpoints.
    /// </summary>
    public static class SwitchStates
    {
        public const string On = "ON";
        public const string Off = "OFF";

        /// <summary>The states accepted when writing switch data.</summary>
        public static readonly string[] Valid = { On, Off };

        public static bool IsValid(string? value) =>
            value != null && Valid.Contains(value, StringComparer.OrdinalIgnoreCase);
    }
}
