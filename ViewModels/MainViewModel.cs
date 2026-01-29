// ViewModels/MainViewModel.cs
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Threading;
using FxVolatilityImport.Models;
using FxVolatilityImport.Services;

namespace FxVolatilityImport.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged
    {
        private readonly LivePositionsService _positionsService = new();
        private readonly BloombergDataService _bbgService = new();
        private readonly Mx3ExportService _exportService = new();
        private readonly SettingsService _settingsService = new();
        private readonly DispatcherTimer _timer;

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

        public ICommand LoadDataCommand { get; }
        public ICommand ImportAtmCommand { get; }
        public ICommand ImportSmileCommand { get; }
        public ICommand RefreshPairsCommand { get; }

        public MainViewModel()
        {
            LoadDataCommand = new RelayCommand(_ => LoadData());
            ImportAtmCommand = new RelayCommand(_ => ExportAtm());
            ImportSmileCommand = new RelayCommand(_ => ExportSmile());
            RefreshPairsCommand = new RelayCommand(_ => RefreshCurrencyPairs());

            // Ladda sparade inställningar
            LoadSettings();

            // Timer som kollar varje sekund
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += OnTimerTick;
            _timer.Start();
        }

        private void OnTimerTick(object? sender, EventArgs e)
        {
            if (!AutoImportEnabled) return;

            var now = DateTime.Now;

            // Kör vid :15 över varje timme mellan 08-16
            if (now.Minute == 15 && now.Second == 0 &&
                now.Hour >= 8 && now.Hour <= 16)
            {
                LoadData();
                System.Threading.Thread.Sleep(5000);
                ExportAtm();
                ExportSmile();
            }
        }

        public void RefreshCurrencyPairs()
        {
            var livePairs = _positionsService.GetUniqueCurrencyPairs();
            var settings = _settingsService.Load();

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
            StatusText = $"Found {livePairs.Count} currency pairs from live positions";
        }

        private void LoadData()
        {
            StatusText = "Loading data from Bloomberg...";
            var configs = CurrencyPairs.Select(p => new CurrencyPairConfig
            {
                CurrencyPair = p.CurrencyPair,
                AtmSource = p.AtmSource,
                SmileSource = p.SmileSource,
                IsLive = p.IsLive
            }).ToList();

            var data = _bbgService.LoadVolatilityData(configs);

            VolatilityData.Clear();
            foreach (var item in data)
                VolatilityData.Add(item);

            StatusText = $"Loaded {data.Count} tenor points";
            SaveSettings();
        }

        private void ExportAtm()
        {
            _exportService.ExportAtm(VolatilityData.ToList());
            LastImportTime = $"ATM imported at {DateTime.Now:HH:mm:ss}";
        }

        private void ExportSmile()
        {
            _exportService.ExportSmile(VolatilityData.ToList());
            LastImportTime = $"Smile imported at {DateTime.Now:HH:mm:ss}";
        }

        private void LoadSettings()
        {
            var settings = _settingsService.Load();
            foreach (var config in settings.CurrencyPairs)
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

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
