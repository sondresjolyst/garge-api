using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace garge_api.Models.Anonymized
{
    /// <summary>
    /// One anonymized reading belonging to an <see cref="AnonymizedSeries"/>. Numeric for every
    /// source type: sensor values are parsed from their string form; switch on/off maps to 1/0.
    /// Carries no device or user identifier — only the surrogate <see cref="SeriesId"/>.
    /// </summary>
    public class AnonymizedReading
    {
        public long Id { get; set; }

        public long SeriesId { get; set; }

        [ForeignKey(nameof(SeriesId))]
        public AnonymizedSeries? Series { get; set; }

        public double Value { get; set; }

        public DateTime Timestamp { get; set; }
    }
}
