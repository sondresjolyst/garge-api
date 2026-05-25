namespace garge_api.Models.Common
{
    /// <summary>
    /// A telemetry row that carries the instant it was recorded. Implemented by SensorData and
    /// SwitchData so the shared time-range query filter (<see
    /// cref="garge_api.Helpers.TimeRangeQueryExtensions"/>) can bound either by timestamp without
    /// duplicating the filter per entity.
    /// </summary>
    public interface ITimestamped
    {
        DateTime Timestamp { get; }
    }
}
