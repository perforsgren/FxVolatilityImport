// Services/BloombergDataService.cs
using FxVolatilityImport.Models;
using SwedTools;

namespace FxVolatilityImport.Services
{
    public class BloombergDataService
    {
        private readonly string[] _tenors =
            { "ON", "1W", "2W", "1M", "2M", "3M", "6M", "1Y", "2Y", "3Y" };

        public List<VolatilityTenor> LoadVolatilityData(List<CurrencyPairConfig> pairs)
        {
            var result = new List<VolatilityTenor>();
            var livePairs = pairs.Where(p => p.IsLive).ToList();

            if (!livePairs.Any()) return result;

            var bbg = new SwedBloombergApi();
            bbg.startBloombergSession();

            // === ATM data ===
            var atmTickers = new List<string>();
            foreach (var pair in livePairs)
            {
                foreach (var tenor in _tenors)
                {
                    atmTickers.Add($"{pair.CurrencyPair}V{tenor} {pair.AtmSource} Curncy");
                }
            }

            bbg.getRefData(atmTickers, new List<string> { "PX_BID", "PX_ASK" });

            // Skapa tenor-objekt med ATM-data
            int idx = 0;
            foreach (var pair in livePairs)
            {
                foreach (var tenor in _tenors)
                {
                    var ticker = atmTickers[idx];
                    result.Add(new VolatilityTenor
                    {
                        CurrencyPair = pair.CurrencyPair,
                        Tenor = tenor,
                        AtmBid = ParseDouble(bbg.getValue(ticker, "PX_BID")),
                        AtmAsk = ParseDouble(bbg.getValue(ticker, "PX_ASK"))
                    });
                    idx++;
                }
            }

            // === Smile data (RR/BF) ===
            var smileTickers = new List<string>();
            foreach (var pair in livePairs)
            {
                foreach (var tenor in _tenors)
                {
                    smileTickers.Add($"{pair.CurrencyPair}25R{tenor} {pair.SmileSource} Curncy");
                    smileTickers.Add($"{pair.CurrencyPair}10R{tenor} {pair.SmileSource} Curncy");
                    smileTickers.Add($"{pair.CurrencyPair}25B{tenor} {pair.SmileSource} Curncy");
                    smileTickers.Add($"{pair.CurrencyPair}10B{tenor} {pair.SmileSource} Curncy");
                }
            }

            bbg.getRefData(smileTickers, new List<string> { "PX_MID" });

            // Fyll i smile-data
            idx = 0;
            int tenorIdx = 0;
            for (int i = 0; i < smileTickers.Count; i += 4)
            {
                result[tenorIdx].RR25D = ParseDouble(bbg.getValue(smileTickers[i], "PX_MID"));
                result[tenorIdx].RR10D = ParseDouble(bbg.getValue(smileTickers[i + 1], "PX_MID"));
                result[tenorIdx].BF25D = ParseDouble(bbg.getValue(smileTickers[i + 2], "PX_MID"));
                result[tenorIdx].BF10D = ParseDouble(bbg.getValue(smileTickers[i + 3], "PX_MID"));
                tenorIdx++;
            }

            return result;
        }

        private double ParseDouble(string value)
        {
            return double.TryParse(value, out var d) ? d : 0;
        }
    }
}
