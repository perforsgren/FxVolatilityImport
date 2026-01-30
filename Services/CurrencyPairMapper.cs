// Services/CurrencyPairMapper.cs
namespace FxVolatilityImport.Services
{
    public static class CurrencyPairMapper
    {
        // Mappning: RiskSystem-par -> Bloomberg-par
        // Om paret finns här är det inverterat i Bloomberg
        private static readonly Dictionary<string, string> _invertedPairs = new(StringComparer.OrdinalIgnoreCase)
        {
            { "CNHSEK", "SEKCNH" },
            // Lägg till fler vid behov, t.ex.:
            // { "JPYSEK", "SEKJPY" },
        };

        /// <summary>
        /// Konverterar från risk system-format till Bloomberg-format
        /// </summary>
        public static string ToBloombergPair(string riskSystemPair)
        {
            if (_invertedPairs.TryGetValue(riskSystemPair, out var bbgPair))
                return bbgPair;

            return riskSystemPair;
        }

        /// <summary>
        /// Konverterar från Bloomberg-format till risk system-format (för MX3 export)
        /// </summary>
        public static string ToRiskSystemPair(string bloombergPair)
        {
            // Omvänd lookup
            var match = _invertedPairs.FirstOrDefault(kvp =>
                kvp.Value.Equals(bloombergPair, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrEmpty(match.Key))
                return match.Key;

            return bloombergPair;
        }

        /// <summary>
        /// Returnerar true om paret är inverterat i Bloomberg
        /// </summary>
        public static bool IsInverted(string riskSystemPair)
        {
            return _invertedPairs.ContainsKey(riskSystemPair);
        }

        /// <summary>
        /// Formaterar för MX3 XML (t.ex. "EURSEK" -> "EUR/SEK", "CNHSEK" -> "CNH/SEK")
        /// </summary>
        public static string ToMx3Format(string pair)
        {
            // Använd alltid risk system-paret för MX3
            var rsPair = ToRiskSystemPair(pair);
            return $"{rsPair.Substring(0, 3)}/{rsPair.Substring(3, 3)}";
        }

        /// <summary>
        /// Justerar RR-värde om paret är inverterat (byter tecken)
        /// </summary>
        public static double AdjustRiskReversal(double rr, string riskSystemPair)
        {
            return IsInverted(riskSystemPair) ? -rr : rr;
        }
    }
}
