// ViewModels/MainViewModel.cs
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using FxVolatilityImport.Models;
using FxVolatilityImport.Services;
using Application = System.Windows.Application;

namespace FxVolatilityImport.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly LivePositionsService _positionsService = new();
        private readonly BloombergService _bbgService = new();
        private readonly Mx3ExportService _exportService = new();
        private readonly SettingsService _settingsService = new();
        private readonly DispatcherTimer _timer;
        private readonly DispatcherTimer _successFadeTimer;
        private readonly FileSystemWatcher _fileWatcher;
        private readonly FileSystemWatcher _positionsFileWatcher;

        private bool _atmImportPending;
        private bool _smileImportPending;
        private bool _atmExportPending;
        private bool _smileExportPending;
        private string? _atmImportCompletedTime;
        private string? _smileImportCompletedTime;
        private int _lastScheduledHour = -1;
        private DateTime _lastPositionsFileCheck = DateTime.MinValue;

        // Sökvägar för att kunna polla filerna
        private readonly string _outputDir = @"\\sto-file23.fspa.myntet.se\NTSHARE\MX3_INTRA_DAY_MARKETDATA\";
        private readonly string _positionsFilePath = @"\\sto-file23.fspa.myntet.se\NTSHARE\MX3\FXD_LIVE_OPTIONS\fxd_live_opt.csv";
        private const string AtmFileName = "update_fxvols_ps.xml";
        private const string SmileFileName = "update_fxvols_smile.xml";

        public ObservableCollection<CurrencyPairViewModel> CurrencyPairs { get; } = new();
        public ObservableCollection<VolatilityTenor> VolatilityData { get; } = new();

        private string _statusText = "Ready";
        public string StatusText
        {
            get => _statusText;
            set { _statusText = value; OnPropertyChanged(); }
        }

        private string _lastImportTime = "";
        public string LastImportTime
        {
            get => _lastImportTime;
            set { _lastImportTime = value; OnPropertyChanged(); }
        }

        private bool _autoImportEnabled = true;
        public bool AutoImportEnabled
        {
            get => _autoImportEnabled;
            set { _autoImportEnabled = value; OnPropertyChanged(); }
        }

        private bool _isConnected;
        public bool IsConnected
        {
            get => _isConnected;
            set { _isConnected = value; OnPropertyChanged(); }
        }

        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            set { _isLoading = value; OnPropertyChanged(); }
        }

        private bool _isImporting;
        public bool IsImporting
        {
            get => _isImporting;
            set { _isImporting = value; OnPropertyChanged(); }
        }

        private bool _importJustCompleted;
        public bool ImportJustCompleted
        {
            get => _importJustCompleted;
            set { _importJustCompleted = value; OnPropertyChanged(); }
        }

        public ICommand LoadDataCommand { get; }
        public ICommand ImportAtmCommand { get; }
        public ICommand ImportSmileCommand { get; }
        public ICommand RefreshPairsCommand { get; }
        public ICommand ConnectCommand { get; }

        public MainViewModel()
        {
            LoadDataCommand = new RelayCommand(_ => LoadDataAsync(), _ => !IsLoading);
            ImportAtmCommand = new RelayCommand(_ => ExportAtm(), _ => VolatilityData.Any());
            ImportSmileCommand = new RelayCommand(_ => ExportSmile(), _ => VolatilityData.Any());
            RefreshPairsCommand = new RelayCommand(_ => RefreshCurrencyPairs());
            ConnectCommand = new RelayCommand(_ => Connect());

            LoadSettings();

            // Timer varje sekund för schemalagd körning OCH för att polla filstatus
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += OnTimerTick;
            _timer.Start();

            // Timer för att dölja success-indikatorn efter några sekunder
            _successFadeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _successFadeTimer.Tick += (s, e) =>
            {
                ImportJustCompleted = false;
                _successFadeTimer.Stop();
            };

            // FileWatcher för att övervaka importfiler
            _fileWatcher = new FileSystemWatcher(_outputDir)
            {
                Filter = "*.xml",
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
                InternalBufferSize = 65536 // Öka buffern för nätverksdelningar
            };
            _fileWatcher.Deleted += OnFileDeleted;
            _fileWatcher.Created += OnFileCreated;
            _fileWatcher.Renamed += OnFileRenamed;

            // FileWatcher för live positions-filen
            var positionsDir = Path.GetDirectoryName(_positionsFilePath)!;
            _positionsFileWatcher = new FileSystemWatcher(positionsDir)
            {
                Filter = Path.GetFileName(_positionsFilePath),
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
                InternalBufferSize = 65536
            };
            _positionsFileWatcher.Changed += OnPositionsFileChanged;
            _positionsFileWatcher.Created += OnPositionsFileChanged;

            // Spara initial tidsstämpel för positions-filen
            _lastPositionsFileCheck = GetPositionsFileLastModified();
        }

        private DateTime GetPositionsFileLastModified()
        {
            try
            {
                return File.Exists(_positionsFilePath)
                    ? File.GetLastWriteTime(_positionsFilePath)
                    : DateTime.MinValue;
            }
            catch
            {
                return DateTime.MinValue;
            }
        }

        private void OnFileCreated(object sender, FileSystemEventArgs e)
        {
            var fileName = Path.GetFileName(e.FullPath);
            HandleFileAppeared(fileName);
        }

        private void OnFileDeleted(object sender, FileSystemEventArgs e)
        {
            var fileName = Path.GetFileName(e.FullPath);
            HandleFileDisappeared(fileName);
        }

        private void OnFileRenamed(object sender, RenamedEventArgs e)
        {
            var oldFileName = Path.GetFileName(e.OldFullPath);
            HandleFileDisappeared(oldFileName);
        }

        private void HandleFileAppeared(string fileName)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (fileName.Equals(AtmFileName, StringComparison.OrdinalIgnoreCase))
                {
                    _atmImportPending = true;
                    _atmExportPending = false;
                }
                else if (fileName.Equals(SmileFileName, StringComparison.OrdinalIgnoreCase))
                {
                    _smileImportPending = true;
                    _smileExportPending = false;
                }

                UpdateImportingState();
                UpdateImportStatus();
            });
        }

        private void HandleFileDisappeared(string fileName)
        {
            var now = DateTime.Now.ToString("HH:mm:ss");

            Application.Current.Dispatcher.Invoke(() =>
            {
                if (fileName.Equals(AtmFileName, StringComparison.OrdinalIgnoreCase) && _atmImportPending)
                {
                    _atmImportPending = false;
                    _atmImportCompletedTime = now;
                }
                else if (fileName.Equals(SmileFileName, StringComparison.OrdinalIgnoreCase) && _smileImportPending)
                {
                    _smileImportPending = false;
                    _smileImportCompletedTime = now;
                }

                UpdateImportingState();
                UpdateImportStatus();
            });
        }

        private void UpdateImportingState()
        {
            var wasImporting = IsImporting;
            IsImporting = _atmExportPending || _smileExportPending || _atmImportPending || _smileImportPending;

            if (wasImporting && !IsImporting)
            {
                ImportJustCompleted = true;
                _successFadeTimer.Stop();
                _successFadeTimer.Start();
            }
        }

        private bool _lastAtmFileExists;
        private bool _lastSmileFileExists;

        private void CheckFileStatus()
        {
            try
            {
                var atmExists = File.Exists(Path.Combine(_outputDir, AtmFileName));
                var smileExists = File.Exists(Path.Combine(_outputDir, SmileFileName));

                if (atmExists && !_lastAtmFileExists)
                {
                    HandleFileAppeared(AtmFileName);
                }
                else if (!atmExists && _lastAtmFileExists && _atmImportPending)
                {
                    HandleFileDisappeared(AtmFileName);
                }

                if (smileExists && !_lastSmileFileExists)
                {
                    HandleFileAppeared(SmileFileName);
                }
                else if (!smileExists && _lastSmileFileExists && _smileImportPending)
                {
                    HandleFileDisappeared(SmileFileName);
                }

                _lastAtmFileExists = atmExists;
                _lastSmileFileExists = smileExists;
            }
            catch
            {
            }
        }

        private void CheckPositionsFileChanged()
        {
            try
            {
                var currentModified = GetPositionsFileLastModified();
                
                if (currentModified > _lastPositionsFileCheck)
                {
                    _lastPositionsFileCheck = currentModified;
                    
                    // Vänta lite så filen hinner skrivas klart
                    Task.Delay(500).ContinueWith(_ =>
                    {
                        Application.Current.Dispatcher.Invoke(RefreshCurrencyPairs);
                    });
                }
            }
            catch
            {
            }
        }

        private void UpdateExportStatus()
        {
            if (_atmExportPending && _smileExportPending)
            {
                StatusText = "ATM and Smile exported, waiting for MX3 import...";
            }
            else if (_atmExportPending)
            {
                StatusText = "ATM exported, waiting for MX3 import...";
            }
            else if (_smileExportPending)
            {
                StatusText = "Smile exported, waiting for MX3 import...";
            }
        }

        private void UpdateImportStatus()
        {
            if (_atmExportPending || _smileExportPending)
            {
                UpdateExportStatus();
                return;
            }

            if (_atmImportPending && _smileImportPending)
            {
                StatusText = "ATM and Smile importing to MX3...";
                return;
            }

            if (_atmImportPending && !_smileImportPending)
            {
                StatusText = _smileImportCompletedTime != null
                    ? $"Smile done at {_smileImportCompletedTime}, ATM importing..."
                    : "ATM importing to MX3...";
                return;
            }

            if (!_atmImportPending && _smileImportPending)
            {
                StatusText = _atmImportCompletedTime != null
                    ? $"ATM done at {_atmImportCompletedTime}, Smile importing..."
                    : "Smile importing to MX3...";
                return;
            }

            if (_atmImportCompletedTime != null && _smileImportCompletedTime != null)
            {
                StatusText = $"ATM and Smile completed at {_atmImportCompletedTime}";
                _atmImportCompletedTime = null;
                _smileImportCompletedTime = null;
            }
            else if (_atmImportCompletedTime != null)
            {
                StatusText = $"ATM completed at {_atmImportCompletedTime}";
                _atmImportCompletedTime = null;
            }
            else if (_smileImportCompletedTime != null)
            {
                StatusText = $"Smile completed at {_smileImportCompletedTime}";
                _smileImportCompletedTime = null;
            }
        }

        private void OnTimerTick(object? sender, EventArgs e)
        {
            // Polla filstatus varje sekund
            if (_atmExportPending || _smileExportPending || _atmImportPending || _smileImportPending)
            {
                CheckFileStatus();
            }

            // Polla positions-filen var 5:e sekund (backup för FileSystemWatcher)
            if (DateTime.Now.Second % 5 == 0)
            {
                CheckPositionsFileChanged();
            }

            if (!AutoImportEnabled) return;

            var now = DateTime.Now;

            if (now.Minute == 15 &&
                now.Second == 0 &&
                now.Hour >= 8 &&
                now.Hour <= 16 &&
                now.Hour != _lastScheduledHour)
            {
                _lastScheduledHour = now.Hour;
                RunScheduledImport();
            }
        }

        private async void RunScheduledImport()
        {
            StatusText = $"Scheduled import at {DateTime.Now:HH:mm}...";

            await LoadDataAsyncInternal();

            if (VolatilityData.Any())
            {
                ExportAtm();
                ExportSmile();
            }
            else
            {
                StatusText = "Scheduled import failed - no data loaded";
            }
        }

        private void Connect()
        {
            StatusText = "Connecting to Bloomberg...";
            IsConnected = _bbgService.Connect();
            StatusText = IsConnected
                ? "Connected to Bloomberg"
                : "Failed to connect to Bloomberg";
        }

        private void OnPositionsFileChanged(object sender, FileSystemEventArgs e)
        {
            // Uppdatera tidsstämpeln och vänta lite innan refresh
            _lastPositionsFileCheck = DateTime.Now;
            
            Task.Delay(1000).ContinueWith(_ =>
            {
                Application.Current.Dispatcher.Invoke(RefreshCurrencyPairs);
            });
        }

        public void RefreshCurrencyPairs()
        {
            var livePairs = _positionsService.GetUniqueCurrencyPairs(); // Redan sorterad
            var settings = _settingsService.Load();

            // Ta bort par som inte längre finns
            var toRemove = CurrencyPairs
                .Where(p => !livePairs.Contains(p.CurrencyPair))
                .ToList();
            foreach (var p in toRemove)
                CurrencyPairs.Remove(p);

            // Lägg till nya par
            foreach (var pair in livePairs)
            {
                if (!CurrencyPairs.Any(p => p.CurrencyPair == pair))
                {
                    var existing = settings.CurrencyPairs
                        .FirstOrDefault(c => c.CurrencyPair == pair);

                    CurrencyPairs.Add(new CurrencyPairViewModel
                    {
                        CurrencyPair = pair,
                        AtmSource = existing?.AtmSource ?? "BGN",
                        SmileSource = existing?.SmileSource ?? "BGN",
                        IsLive = existing?.IsLive ?? true
                    });
                }
            }

            // Sortera listan i alfabetisk ordning
            var sorted = CurrencyPairs.OrderBy(p => p.CurrencyPair).ToList();
            CurrencyPairs.Clear();
            foreach (var pair in sorted)
            {
                CurrencyPairs.Add(pair);
            }

            var fileDate = _positionsService.GetFileLastModified();
            StatusText = $"Found {livePairs.Count} pairs (file: {fileDate:yyyy-MM-dd HH:mm})";
            SaveSettings();
        }

        private async Task LoadDataAsyncInternal()
        {
            if (!IsConnected)
            {
                Connect();
                if (!IsConnected)
                {
                    StatusText = "Cannot load data - not connected to Bloomberg";
                    return;
                }
            }

            IsLoading = true;
            StatusText = "Loading volatility data from Bloomberg...";

            try
            {
                var configs = CurrencyPairs.Select(p => new CurrencyPairConfig
                {
                    CurrencyPair = p.CurrencyPair,
                    AtmSource = p.AtmSource,
                    SmileSource = p.SmileSource,
                    IsLive = p.IsLive
                }).ToList();

                var data = await Task.Run(() => _bbgService.LoadVolatilityData(configs));

                VolatilityData.Clear();
                foreach (var item in data)
                    VolatilityData.Add(item);

                StatusText = $"Loaded {data.Count} tenor points at {DateTime.Now:HH:mm:ss}";
                SaveSettings();
            }
            catch (Exception ex)
            {
                StatusText = $"Error loading data: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        private async void LoadDataAsync()
        {
            await LoadDataAsyncInternal();
        }

        private void ExportAtm()
        {
            try
            {
                _exportService.ExportAtm(VolatilityData.ToList());
                _atmExportPending = true;
                _lastAtmFileExists = true;
                UpdateImportingState();
                UpdateExportStatus();
            }
            catch (Exception ex)
            {
                StatusText = $"ATM export error: {ex.Message}";
            }
        }

        private void ExportSmile()
        {
            try
            {
                _exportService.ExportSmile(VolatilityData.ToList());
                _smileExportPending = true;
                _lastSmileFileExists = true;
                UpdateImportingState();
                UpdateExportStatus();
            }
            catch (Exception ex)
            {
                StatusText = $"Smile export error: {ex.Message}";
            }
        }

        private void LoadSettings()
        {
            var settings = _settingsService.Load();
            
            // Ladda och sortera direkt
            var sorted = settings.CurrencyPairs.OrderBy(c => c.CurrencyPair).ToList();
            
            foreach (var config in sorted)
            {
                CurrencyPairs.Add(new CurrencyPairViewModel
                {
                    CurrencyPair = config.CurrencyPair,
                    AtmSource = config.AtmSource,
                    SmileSource = config.SmileSource,
                    IsLive = config.IsLive
                });
            }
        }

        private void SaveSettings()
        {
            var settings = new AppSettings
            {
                CurrencyPairs = CurrencyPairs.Select(p => new CurrencyPairConfig
                {
                    CurrencyPair = p.CurrencyPair,
                    AtmSource = p.AtmSource,
                    SmileSource = p.SmileSource,
                    IsLive = p.IsLive
                }).ToList()
            };
            _settingsService.Save(settings);
        }

        public void Dispose()
        {
            _timer.Stop();
            _successFadeTimer.Stop();
            _fileWatcher.Dispose();
            _positionsFileWatcher.Dispose();
            _bbgService.Dispose();
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
