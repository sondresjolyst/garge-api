namespace garge_api.Services
{
    /// <summary>
    /// Prices are what the customer pays. With VAT on, the VAT is part of that price, so switching
    /// VAT on or off does not change what customers pay.
    /// </summary>
    public static class Pricing
    {
        public const int VatPercent = 25;
        public const int VatBasisPoints = 2500;

        public static int VatPercentFor(bool vatEnabled) => vatEnabled ? VatPercent : 0;

        /// <summary>
        /// Splits a price into the amount excluding VAT and the VAT. The VAT is the price times
        /// rate / (100 + rate), rounded to whole øre, and the two parts always add up to the price.
        /// </summary>
        public static (int ExclVat, int Vat) Split(int priceInOre, int vatPercent)
        {
            if (vatPercent == 0) return (priceInOre, 0);
            var exclVat = (int)Math.Round(priceInOre * 100m / (100 + vatPercent), MidpointRounding.AwayFromZero);
            return (exclVat, priceInOre - exclVat);
        }
    }
}
