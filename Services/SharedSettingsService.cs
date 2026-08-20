// Services/SharedSettingsService.cs
using System.IO;
using System.Text.Json;
using FxVolatilityImport.Models;

namespace FxVolatilityImport.Services
{
    public class SharedSettingsService : IDisposable
    {
        private readonly string _sharedSettingsPath = @"\\nas-se11.fspa.myntet.se\MUREX\PROD\FX\Settings\VolatilityImport\vol_import_settings.json";

        private readonly FileSystemWatcher _watcher;
        private readonly object _lock = new();
        private DateTime _lastWriteTime = DateTime.MinValue;

        public event EventHandler? SettingsChanged;

        public SharedSettingsService()
        {
            var dir = Path.GetDirectoryName(_sharedSettingsPath)!;
            var fileName = Path.GetFileName(_sharedSettingsPath);

            _watcher = new FileSystemWatcher(dir, fileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                EnableRaisingEvents = true
            };

            _watcher.Changed += OnFileChanged;
            _watcher.Created += OnFileChanged;
        }

        private void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            // Debounce - ignorera om vi själva precis skrev
            var writeTime = File.GetLastWriteTime(_sharedSettingsPath);
            if ((writeTime - _lastWriteTime).TotalSeconds < 2)
                return;

            // Vänta lite så filen hinner skrivas klart
            Thread.Sleep(500);
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }

        public SharedSettings Load()
        {
            lock (_lock)
            {
                try
                {
                    if (File.Exists(_sharedSettingsPath))
                    {
                        var json = File.ReadAllText(_sharedSettingsPath);
                        return JsonSerializer.Deserialize<SharedSettings>(json) ?? new SharedSettings();
                    }
                }
                catch (Exception)
                {
                    // Filen kan vara låst av annan användare
                }
                return new SharedSettings();
            }
        }

        public void Save(SharedSettings settings)
        {
            lock (_lock)
            {
                try
                {
                    settings.LastModified = DateTime.Now;
                    settings.LastModifiedBy = Environment.UserName;

                    var dir = Path.GetDirectoryName(_sharedSettingsPath);
                    if (!Directory.Exists(dir))
                        Directory.CreateDirectory(dir!);

                    var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
                    {
                        WriteIndented = true
                    });

                    File.WriteAllText(_sharedSettingsPath, json);
                    _lastWriteTime = DateTime.Now;
                }
                catch (Exception)
                {
                    // Hantera om filen är låst
                }
            }
        }

        public void UpdatePairSource(string currencyPair, string atmSource, string smileSource)
        {
            var settings = Load();

            var existing = settings.PairSources.FirstOrDefault(p =>
                p.CurrencyPair.Equals(currencyPair, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                existing.AtmSource = atmSource;
                existing.SmileSource = smileSource;
                existing.LastUsed = DateTime.Now;
            }
            else
            {
                settings.PairSources.Add(new PairSourceConfig
                {
                    CurrencyPair = currencyPair,
                    AtmSource = atmSource,
                    SmileSource = smileSource,
                    LastUsed = DateTime.Now
                });
            }

            Save(settings);
        }

        public PairSourceConfig? GetPairSource(string currencyPair)
        {
            var settings = Load();
            return settings.PairSources.FirstOrDefault(p =>
                p.CurrencyPair.Equals(currencyPair, StringComparison.OrdinalIgnoreCase));
        }

        public void Dispose()
        {
            _watcher.Dispose();
        }
    }
}