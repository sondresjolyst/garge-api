using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace garge_api.Models.Switch
{
    /// <summary>
    /// One row per (user, switch) DIRECT ownership stint. Bounds which switch telemetry a direct
    /// owner may read so a new owner of a re-claimed/resold switch never sees the previous owner's
    /// history. Indirect access (via the discovered-device chain from an owned sensor) is bounded by
    /// the relevant <see cref="Sensor.SensorOwnershipPeriod"/> instead, not by a row here.
    ///
    /// <para>The first-ever direct owner gets <see cref="FirstOwnerStart"/> so they keep any
    /// pre-claim history; every subsequent (resale) owner starts at their claim time.</para>
    /// </summary>
    public class SwitchOwnershipPeriod
    {
        /// <summary>Sentinel start for the first-ever owner so they see all history from the switch's birth.</summary>
        public static readonly DateTime FirstOwnerStart = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public int Id { get; set; }

        [Required]
        public required string UserId { get; set; }

        public int SwitchId { get; set; }

        public DateTime StartedAt { get; set; }

        public DateTime? EndedAt { get; set; }

        [ForeignKey(nameof(UserId))]
        public User? User { get; set; }

        [ForeignKey(nameof(SwitchId))]
        public Switch? Switch { get; set; }
    }
}
