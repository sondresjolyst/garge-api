using System.ComponentModel.DataAnnotations;

namespace garge_api.Models.Pipeline
{
    /// <summary>A period when sensor readings could not reach the API (operator, broker or API down).</summary>
    public class PipelineGap
    {
        public int Id { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime? EndedAt { get; set; }

        [MaxLength(16)]
        public required string Source { get; set; }

        public DateTime? AdminNotifiedAt { get; set; }
        public DateTime? AllClearSentAt { get; set; }
    }
}
