using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace garge_api.Models.Admin
{
    /// <summary>
    /// One immutable per-day snapshot of platform totals. Each day's row is computed once from live
    /// data and never rewritten, so the stats history survives even after the per-user rows it was
    /// derived from are purged (post-5y retention). Holds only aggregate counts — no identifiers — so
    /// it is anonymous and may be kept indefinitely.
    /// </summary>
    public class DailyStatSnapshot
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        /// <summary>The UTC calendar day this snapshot represents. Unique.</summary>
        public DateOnly Date { get; set; }

        public int TotalUsers { get; set; }
        public int TotalSensors { get; set; }
        public int TotalSwitches { get; set; }
        public int TotalAutomations { get; set; }
    }
}
