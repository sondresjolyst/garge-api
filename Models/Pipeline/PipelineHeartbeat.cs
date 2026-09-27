namespace garge_api.Models.Pipeline
{
    /// <summary>Single-row (Id = 1) record of garge-operator's heartbeat and the detector's last run.</summary>
    public class PipelineHeartbeat
    {
        public int Id { get; set; } = 1;
        public DateTime? LastHeartbeatAt { get; set; }
        public bool MqttConnected { get; set; }
        public DateTime? LastHealthyHeartbeatAt { get; set; }
        public DateTime? LastDetectorTickAt { get; set; }
    }
}
