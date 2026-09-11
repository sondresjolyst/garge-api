namespace garge_api.Constants
{
    public static class SecurityMode
    {
        public const int ShortSleepSeconds = 600;
        public const int LongSleepSeconds = 3600;
        public const int DefaultThresholdMinutes = 25;
        public const int MinThresholdMinutes = 25;
        public const int MaxThresholdMinutes = 180;
        public const int FloorMarginMillivolts = 100;
        public static readonly TimeSpan OfflineDisarmAfter = TimeSpan.FromDays(7);
        public static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromMinutes(2);
        public static readonly TimeSpan AdminGapNotifyAfter = TimeSpan.FromMinutes(5);

        public static class GapSources
        {
            public const string Operator = "operator";
            public const string Api = "api";
        }

        public static class States
        {
            public const string Off = "off";
            public const string Pending = "pending";
            public const string Armed = "armed";
            public const string PausedLowBattery = "paused_low_battery";
            public const string Offline = "offline";
        }

        public static class Reasons
        {
            public const string FirmwareTooOld = "firmware_too_old";
            public const string AwaitingWake = "awaiting_wake";
            public const string LowBattery = "low_battery";
        }

        public static class ErrorCodes
        {
            public const string ChargingAutomationRequired = "charging_automation_required";
            public const string NoAlertChannel = "no_alert_channel";
            public const string InvalidThreshold = "invalid_threshold";
            public const string UnsupportedSensor = "unsupported_sensor";
            public const string SecurityNeedsAlertChannel = "security_needs_alert_channel";
        }
    }

    public static class NotificationKinds
    {
        public const string Offline = "offline";
        public const string Security = "security";
    }
}
