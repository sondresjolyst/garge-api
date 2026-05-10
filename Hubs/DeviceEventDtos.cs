namespace garge_api.Hubs
{
    /// <summary>Hub-wire DTO for switch state events. Excludes EF nav properties
    /// like RegistrationCode that should never reach clients.</summary>
    public record SwitchEventDto(
        int Id,
        int SwitchId,
        string Value,
        DateTime Timestamp,
        SwitchSummaryDto? Switch);

    public record SwitchSummaryDto(int Id, string Name, string Type);

    /// <summary>Hub-wire DTO for sensor data events. Excludes EF nav properties
    /// like RegistrationCode and ParentName that should never reach clients.</summary>
    public record SensorEventDto(
        int Id,
        int SensorId,
        string Value,
        DateTime Timestamp,
        SensorSummaryDto? Sensor);

    public record SensorSummaryDto(int Id, string Name, string Type);
}
