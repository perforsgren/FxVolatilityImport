// Services/SharedSettingsService.cs
using System.IO;
using System.Text;
using System.Text.Json;
using FxVolatilityImport.Models;

namespace FxVolatilityImport.Services
{
    /// <summary>
    /// Delade inställningar (sources, vilka par som ingår, schemalagd import av/på) i en JSON-fil på nätverksdisken.
    ///
    /// - Pollar filens tidsstämpel var 5:e sekund i stället för FileSystemWatcher, som är opålitlig
    ///   på nätverksdiskar (tappar händelser och slutar tyst fungera efter nätverksavbrott).
    /// - Skriver atomärt (temporärfil + rename) så att ingen läser en halvskriven fil.
    /// - Läser in senaste versionen från disk innan varje ändring, så att ändringar från andra inte skrivs över.
    /// - Läsning sker från en minneskopia, så UI:t aldrig väntar på nätverket.
    /// </summary>
    public sealed class SharedSettingsService : IDisposable
    {
        public const string DefaultPath =
            @"\\nas-se11.fspa.myntet.se\MUREX\PROD\FX\Settings\VolatilityImport\vol_import_settings.json";

        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private readonly string _path;
        private readonly AppLog _log;
        private readonly object _ioLock = new();
        private readonly System.Threading.Timer _timer;

        private volatile SharedSettings _current = new();
        private DateTime _knownWriteTimeUtc = DateTime.MinValue;
        private volatile bool _disposed;

        public SharedSettingsService(AppLog log, string? path = null)
        {
            _log = log;
            _path = path ?? DefaultPath;
            _timer = new System.Threading.Timer(_ => Poll(), null, Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>Anropas på bakgrundstråd när en annan användare har ändrat filen.</summary>
        public event EventHandler? SettingsChanged;

        public string SettingsDirectory => Path.GetDirectoryName(_path)!;

        public bool ScheduledImportEnabled => _current.ScheduledImportEnabled;

        /// <summary>Läser filen första gången (blockerar på nätverket – anropa från bakgrundstråd) och startar pollningen.</summary>
        public void Start()
        {
            lock (_ioLock)
            {
                var loaded = TryReadFromDisk(out var writeTime);
                if (loaded != null)
                {
                    _current = loaded;
                    _knownWriteTimeUtc = writeTime;
                }
                else if (File.Exists(_path))
                {
                    _log.Warning("Could not read shared settings – using defaults until the file is readable");
                }
            }

            _timer.Change(PollInterval, PollInterval);
        }

        public PairSourceConfig? GetPair(string currencyPair)
        {
            var match = _current.PairSources.FirstOrDefault(p =>
                p.CurrencyPair.Equals(currencyPair, StringComparison.OrdinalIgnoreCase));

            return match == null
                ? null
                : new PairSourceConfig
                {
                    CurrencyPair = match.CurrencyPair,
                    AtmSource = match.AtmSource,
                    SmileSource = match.SmileSource,
                    IsLive = match.IsLive,
                    LastUsed = match.LastUsed,
                    ModifiedBy = match.ModifiedBy
                };
        }

        /// <summary>Alla sources som förekommer i filen (för att fylla dropdown-listorna).</summary>
        public IReadOnlyList<string> KnownSources()
            => _current.PairSources
                .SelectMany(p => new[] { p.AtmSource, p.SmileSource })
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        public bool UpdatePair(string currencyPair, string atmSource, string smileSource, bool isLive)
            => Update(settings =>
            {
                var existing = settings.PairSources.FirstOrDefault(p =>
                    p.CurrencyPair.Equals(currencyPair, StringComparison.OrdinalIgnoreCase));

                if (existing == null)
                {
                    existing = new PairSourceConfig { CurrencyPair = currencyPair };
                    settings.PairSources.Add(existing);
                }

                existing.AtmSource = atmSource;
                existing.SmileSource = smileSource;
                existing.IsLive = isLive;
                existing.LastUsed = DateTime.Now;
                existing.ModifiedBy = Environment.UserName;
            });

        public bool SetScheduledImportEnabled(bool enabled)
            => Update(settings => settings.ScheduledImportEnabled = enabled);

        /// <summary>Läs-ändra-skriv. Returnerar false om filen inte kunde sparas (ändringen gäller då bara lokalt).</summary>
        private bool Update(Action<SharedSettings> mutate)
        {
            lock (_ioLock)
            {
                var fresh = TryReadFromDisk(out _) ?? Clone(_current);
                mutate(fresh);
                fresh.LastModified = DateTime.Now;
                fresh.LastModifiedBy = Environment.UserName;
                fresh.PairSources = fresh.PairSources
                    .OrderBy(p => p.CurrencyPair, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                _current = fresh;

                try
                {
                    WriteAtomic(fresh);
                    _knownWriteTimeUtc = File.GetLastWriteTimeUtc(_path);
                    return true;
                }
                catch (Exception ex)
                {
                    _log.Error($"Could not save shared settings ({ex.Message}) – the change only applies on this PC for now");
                    return false;
                }
            }
        }

        private void Poll()
        {
            if (_disposed)
                return;

            try
            {
                if (!File.Exists(_path))
                    return;

                var writeTime = File.GetLastWriteTimeUtc(_path);
                SharedSettings? fresh;

                lock (_ioLock)
                {
                    if (writeTime == _knownWriteTimeUtc)
                        return;

                    fresh = TryReadFromDisk(out var readWriteTime);
                    if (fresh == null)
                        return; // mitt i en skrivning – försök igen nästa varv

                    _current = fresh;
                    _knownWriteTimeUtc = readWriteTime;
                }

                if (!string.Equals(fresh.LastModifiedBy, Environment.UserName, StringComparison.OrdinalIgnoreCase))
                    _log.Info($"Shared settings updated by {fresh.LastModifiedBy}");

                SettingsChanged?.Invoke(this, EventArgs.Empty);
            }
            catch
            {
                // Nätverksfel – nästa poll försöker igen
            }
        }

        private SharedSettings? TryReadFromDisk(out DateTime writeTimeUtc)
        {
            writeTimeUtc = DateTime.MinValue;
            try
            {
                if (!File.Exists(_path))
                    return null;

                writeTimeUtc = File.GetLastWriteTimeUtc(_path);
                using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs, Encoding.UTF8);
                return JsonSerializer.Deserialize<SharedSettings>(reader.ReadToEnd());
            }
            catch
            {
                return null;
            }
        }

        private void WriteAtomic(SharedSettings settings)
        {
            var dir = SettingsDirectory;
            Directory.CreateDirectory(dir);

            var tmp = Path.Combine(dir, $".{Path.GetFileName(_path)}.{Environment.MachineName}.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOptions), Encoding.UTF8);

            try
            {
                for (int attempt = 1; ; attempt++)
                {
                    try
                    {
                        File.Move(tmp, _path, overwrite: true);
                        return;
                    }
                    catch (IOException) when (attempt < 5)
                    {
                        Thread.Sleep(200); // någon läser filen just nu
                    }
                }
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); }
                catch { /* städning är best effort */ }
            }
        }

        private static SharedSettings Clone(SharedSettings settings)
            => JsonSerializer.Deserialize<SharedSettings>(JsonSerializer.Serialize(settings))!;

        public void Dispose()
        {
            _disposed = true;
            _timer.Dispose();
        }
    }
}