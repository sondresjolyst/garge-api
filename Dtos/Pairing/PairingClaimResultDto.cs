namespace garge_api.Dtos.Pairing
{
    public class PairingClaimResultDto
    {
        public List<int> ClaimedSensorIds { get; set; } = new();
        public List<int> ClaimedSwitchIds { get; set; } = new();

        /// <summary>
        /// Devices the token's user already owned. Claiming them again changes no
        /// ownership, but it is a successful pairing from the user's side, so these
        /// raise <c>device-created</c> just like a fresh claim.
        /// </summary>
        public List<int> AlreadyOwnedSensorIds { get; set; } = new();
        public List<int> AlreadyOwnedSwitchIds { get; set; } = new();

        /// <summary>Devices left alone because another user owns them.</summary>
        public int Skipped { get; set; }
    }
}
