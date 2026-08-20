// Services/LivePositionsService.cs
using System.IO;

namespace FxVolatilityImport.Services
{
    public class LivePositionsService
    {
        private readonly string _filePath =
            @"\\sto-file23.fspa.myntet.se\NTSHARE\MX3\FXD_LIVE_OPTIONS\fxd_live_opt.csv";

        // Endast dessa typologier ger upphov till volatilitetshämtning.
        // Allt annat (t.ex. FX: Spot Forward) ignoreras.
        private static readonly HashSet<string> _allowedTypologies = new(StringComparer.OrdinalIgnoreCase)
        {
            "FXD: Simple Option",
            "FXD: Barrier Option",
            "FXD: Touch Rebate",
        };

        public List<string> GetUniqueCurrencyPairs()
        {
            var pairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!File.Exists(_filePath))
                return pairs.ToList();

            var lines = File.ReadAllLines(_filePath);
            if (lines.Length < 2) return pairs.ToList();

            var headers = lines[0].Split(';');

            int currPairIndex = Array.FindIndex(headers, h =>
                h.Trim().Equals("CURR_PAIR", StringComparison.OrdinalIgnoreCase));

            int typologyIndex = Array.FindIndex(headers, h =>
                h.Trim().Equals("TYPOLOGY", StringComparison.OrdinalIgnoreCase));

            if (currPairIndex < 0 || typologyIndex < 0)
                return pairs.ToList();

            for (int i = 1; i < lines.Length; i++)
            {
                var cols = lines[i].Split(';');

                if (cols.Length <= Math.Max(currPairIndex, typologyIndex))
                    continue;

                var typology = cols[typologyIndex].Trim();

                if (!_allowedTypologies.Contains(typology))
                    continue;

                var pair = cols[currPairIndex].Trim();
                if (!string.IsNullOrEmpty(pair))
                {
                    var normalizedPair = pair.Replace("/", "");
                    normalizedPair = CurrencyPairMapper.ToRiskSystemPair(normalizedPair);
                    pairs.Add(normalizedPair);
                }
            }

            return pairs.OrderBy(p => p).ToList();
        }

        public DateTime GetFileLastModified()
        {
            return File.Exists(_filePath)
                ? File.GetLastWriteTime(_filePath)
                : DateTime.MinValue;
        }
    }
}