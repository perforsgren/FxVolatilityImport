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

            var headers = SplitCsvLine(lines[0]);

            int currPairIndex = Array.FindIndex(headers, h =>
                h.Trim().Equals("CURR_PAIR", StringComparison.OrdinalIgnoreCase));

            int typologyIndex = Array.FindIndex(headers, h =>
                h.Trim().Equals("TYPOLOGY", StringComparison.OrdinalIgnoreCase));

            if (currPairIndex < 0 || typologyIndex < 0)
                return pairs.ToList();

            for (int i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i]))
                    continue;

                var cols = SplitCsvLine(lines[i]);

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

        /// <summary>
        /// Delar en CSV-rad på ';' men respekterar citattecken, så att ett fält som
        /// t.ex. "Bank; Stockholm" inte förskjuter alla efterföljande kolumner
        /// (vilket annars kan få TYPOLOGY/CURR_PAIR att läsas ur fel kolumn).
        /// </summary>
        private static string[] SplitCsvLine(string line)
        {
            var fields = new List<string>();
            var current = new System.Text.StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];

                if (inQuotes)
                {
                    if (c == '"')
                    {
                        // Dubbelt citattecken inuti ett citerat fält = ett bokstavligt "
                        if (i + 1 < line.Length && line[i + 1] == '"')
                        {
                            current.Append('"');
                            i++;
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else
                    {
                        current.Append(c);
                    }
                }
                else
                {
                    if (c == '"')
                    {
                        inQuotes = true;
                    }
                    else if (c == ';')
                    {
                        fields.Add(current.ToString());
                        current.Clear();
                    }
                    else
                    {
                        current.Append(c);
                    }
                }
            }

            fields.Add(current.ToString());
            return fields.ToArray();
        }
    }
}