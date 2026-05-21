namespace garge_api.Dtos.User
{
    /// <summary>
    /// The caller's current sensor-data retention preference.
    /// </summary>
    public class DataRetentionDto
    {
        /// <summary>
        /// True when the user has opted out of keeping their sensor history after their subscription
        /// lapses (GDPR Art. 21 objection). False (default) keeps history for the lifetime of the claim.
        /// </summary>
        public bool OptOut { get; set; }

        /// <summary>When the opt-out was set, or null if the user has not opted out.</summary>
        public DateTime? OptedOutAt { get; set; }

        /// <summary>Builds the DTO from the user's stored opt-out timestamp (null = retaining).</summary>
        public static DataRetentionDto From(DateTime? optedOutAt) => new()
        {
            OptOut = optedOutAt != null,
            OptedOutAt = optedOutAt
        };
    }

    /// <summary>
    /// Sets or clears the caller's sensor-data retention opt-out.
    /// </summary>
    public class UpdateDataRetentionDto
    {
        /// <summary>True to opt out of post-lapse retention; false to keep the default (retain).</summary>
        public bool OptOut { get; set; }
    }
}
