using System.ComponentModel.DataAnnotations;

namespace garge_api.Models.Anonymized
{
    /// <summary>
    /// A single anonymized telemetry series: the readings from one device over one ownership stint,
    /// retained indefinitely for ML once the personal copy passes its retention cap (or on erasure).
    ///
    /// <para><see cref="Id"/> is a fresh surrogate key with NO stored mapping back to the original
    /// SensorId/SwitchId/UserId, so a later re-claim of the same physical device cannot rejoin this
    /// data. Series are kept independent (never cross-linked by site/gateway) to avoid re-creating a
    /// household fingerprint. This data is only anonymous while that holds — see the GDPR notes.</para>
    /// </summary>
    public class AnonymizedSeries
    {
        public long Id { get; set; }

        /// <summary>voltage | temperature | humidity | socket</summary>
        [Required]
        [MaxLength(50)]
        public required string SourceType { get; set; }

        /// <summary>
        /// Legacy per-sensor voltage calibration offset, retained for series anonymized before sensor
        /// calibration was removed. No longer populated; always null for new series.
        /// </summary>
        public float? CalibrationOffsetV { get; set; }

        public DateTime AnonymizedAt { get; set; } = DateTime.UtcNow;
    }
}
