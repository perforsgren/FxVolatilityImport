// Services/LivePositionsService.cs
using System.IO;

namespace FxVolatilityImport.Services
{
    public class LivePositionsService
    {
        private readonly string _filePath =
            @"\\sto-file23.fspa.myntet.se\NTSHARE\MX3\FXD_LIVE_OPTIONS\fxd_live_opt.csv";

        public List<string> GetUniqueCurrencyPairs()
        {
            var pairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!File.Exists(_filePath))
                return pairs.ToList();

            var lines = File.ReadAllLines(_filePath);
            if (lines.Length < 2) return pairs.ToList();

            // Hitta CURR_PAIR kolumnindex från header
            var headers = lines[0].Split(';');
            int currPairIndex = Array.FindIndex(headers, h =>
                h.Trim().Equals("CURR_PAIR", StringComparison.OrdinalIgnoreCase));

            if (currPairIndex < 0) return pairs.ToList();

            for (int i = 1; i < lines.Length; i++)
            {
                var cols = lines[i].Split(';');
                if (cols.Length > currPairIndex)
                {
                    var pair = cols[currPairIndex].Trim();
                    if (!string.IsNullOrEmpty(pair))
                    {
                        // Konvertera "USD/SEK" -> "USDSEK"
                        pairs.Add(pair.Replace("/", ""));
                    }
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
