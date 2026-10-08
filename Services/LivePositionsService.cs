// Services/LivePositionsService.cs
using System.IO;

namespace FxVolatilityImport.Services
{
    public sealed record LivePositionsSnapshot(IReadOnlyList<string> Pairs, DateTime FileTime, int OptionRows);

    public class LivePositionsService
    {
        public const string DefaultPath =
            @"\\sto-file23.fspa.myntet.se\NTSHARE\MX3\FXD_LIVE_OPTIONS\fxd_live_opt.csv";

        // Endast dessa typologier ger upphov till volatilitetshämtning.
        // Allt annat (t.ex. FX: Spot Forward, FXD: Non Deliv Fwd) ignoreras.
        private static readonly HashSet<string> _allowedTypologies = new(StringComparer.OrdinalIgnoreCase)
        {
            "FXD: Simple Option",
            "FXD: Barrier Option",
            "FXD: Touch Rebate",
        };

        private readonly string _filePath;

        public LivePositionsService(string? filePath = null)
        {
            _filePath = filePath ?? DefaultPath;
        }

        public DateTime GetFileLastModified()
        {
            try
            {
                return File.Exists(_filePath) ? File.GetLastWriteTime(_filePath) : DateTime.MinValue;
            }
            catch
            {
                return DateTime.MinValue;
            }
        }

        /// <summary>
        /// Läser filen och returnerar unika valutapar med optionspositioner.
        /// Kastar om filen inte kan läsas – anroparen behåller då sin nuvarande lista.
        /// </summary>
        public LivePositionsSnapshot Read()
        {
            var fileTime = File.GetLastWriteTime(_filePath);
            var lines = ReadLinesShared(_filePath);
            if (lines.Count == 0)
                throw new InvalidDataException("The live positions file is empty");

            var headers = SplitCsvLine(lines[0]);

            int currPairIndex = Array.FindIndex(headers, h =>
                h.Trim().Equals("CURR_PAIR", StringComparison.OrdinalIgnoreCase));

            int typologyIndex = Array.FindIndex(headers, h =>
                h.Trim().Equals("TYPOLOGY", StringComparison.OrdinalIgnoreCase));

            if (currPairIndex < 0 || typologyIndex < 0)
                throw new InvalidDataException("CURR_PAIR or TYPOLOGY column missing in the live positions file");

            var pairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var optionRows = 0;

            for (int i = 1; i < lines.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i]))
                    continue;

                var cols = SplitCsvLine(lines[i]);
                if (cols.Length <= Math.Max(currPairIndex, typologyIndex))
                    continue;

                if (!_allowedTypologies.Contains(cols[typologyIndex].Trim()))
                    continue;

                var pair = cols[currPairIndex].Trim();
                if (string.IsNullOrEmpty(pair))
                    continue;

                optionRows++;
                var normalizedPair = CurrencyPairMapper.ToRiskSystemPair(pair.Replace("/", ""));
                pairs.Add(normalizedPair.ToUpperInvariant());
            }

            return new LivePositionsSnapshot(pairs.OrderBy(p => p).ToList(), fileTime, optionRows);
        }

        /// <summary>
        /// Läser med FileShare.ReadWrite så att vi kan läsa även när riskssystemet skriver filen,
        /// och försöker igen ett par gånger om den är låst.
        /// </summary>
        private static List<string> ReadLinesShared(string path)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(fs);
                    var lines = new List<string>();
                    string? line;
                    while ((line = reader.ReadLine()) != null)
                        lines.Add(line);
                    return lines;
                }
                catch (IOException) when (attempt < 3)
                {
                    Thread.Sleep(1000);
                }
            }
        }

        /// <summary>
        /// Delar en CSV-rad på ';' men respekterar citattecken, så att ett fält som
        /// t.ex. "Bank; Stockholm" inte förskjuter alla efterföljande kolumner.
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
                else if (c == '"')
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

            fields.Add(current.ToString());
            return fields.ToArray();
        }
    }
}