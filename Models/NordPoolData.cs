using System;
using System.Collections.Generic;

namespace garge_api.Models
{
    public class PriceResponse
    {
        public DateTime Start { get; set; }
        public DateTime End { get; set; }
        public DateTime Updated { get; set; }
        public required string Currency { get; set; }
        public required Dictionary<string, AreaPrices> Areas { get; set; }
    }

    public class AreaPrices
    {
        public required List<PriceEntry> Values { get; set; }
    }

    public class PriceEntry
    {
        public DateTime Start { get; set; }
        public DateTime End { get; set; }
        public decimal Value { get; set; }
    }
}
