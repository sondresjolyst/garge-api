using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace garge_api.Models.Sensor
{
    /// <summary>
    /// One row per (user, sensor) ownership stint. Bounds which telemetry a user may read so a new
    /// owner of a re-claimed/resold sensor never sees the previous owner's history.
    ///
    /// <para><see cref="StartedAt"/> is when the stint began; <see cref="EndedAt"/> is when the user
    /// unclaimed/sold, or null while still owned. A user sees telemetry only inside their own period(s)
    /// (an allowlist), so removing one ex-owner never widens another user's view.</para>
    ///
    /// <para>The first-ever owner of a sensor gets <see cref="FirstOwnerStart"/> so they keep any
    /// pre-claim setup readings; every subsequent (resale) owner starts at their claim time.</para>
    /// </summary>
    public class SensorOwnershipPeriod
    {
        /// <summary>Sentinel start for the first-ever owner so they see all history from the sensor's birth.</summary>
        public static readonly DateTime FirstOwnerStart = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public int Id { get; set; }

        [Required]
        public required string UserId { get; set; }

        public int SensorId { get; set; }

        public DateTime StartedAt { get; set; }

        public DateTime? EndedAt { get; set; }

        [ForeignKey(nameof(UserId))]
        public User? User { get; set; }

        [ForeignKey(nameof(SensorId))]
        public Sensor? Sensor { get; set; }
    }
}
