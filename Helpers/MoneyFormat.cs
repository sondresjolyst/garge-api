using System.Globalization;

namespace garge_api.Helpers
{
    public static class MoneyFormat
    {
        public static string Nok(int ore) =>
            (ore / 100.0).ToString("N2", CultureInfo.InvariantCulture);
    }
}
