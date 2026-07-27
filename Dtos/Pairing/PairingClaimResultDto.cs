namespace garge_api.Dtos.Pairing
{
    public class PairingClaimResultDto
    {
        public List<int> ClaimedSensorIds { get; set; } = new();
        public List<int> ClaimedSwitchIds { get; set; } = new();
        public int Skipped { get; set; }
    }
}
