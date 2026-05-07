namespace garge_api.Services
{
    public static class Pricing
    {
        public const int VatPercent = 25;
        public const int VatBasisPoints = 2500;

        public static int EffectiveInOre(int priceInOre, bool vatEnabled) =>
            vatEnabled
                ? (int)Math.Round(priceInOre * (1 + VatPercent / 100.0), MidpointRounding.AwayFromZero)
                : priceInOre;
    }
}
