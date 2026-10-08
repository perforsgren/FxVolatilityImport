// ViewModels/MainViewModel.cs
using System.IO;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Input;
using System.Windows.Threading;
using FxVolatilityImport.Controls;
using FxVolatilityImport.Models;
using FxVolatilityImport.Services;

namespace FxVolatilityImport.ViewModels
{
    public sealed class MainViewModel : ObservableObject, IDisposable
    {
        private const int MaxLogEntries = 300;
        private static readonly string[] DefaultSources = { "BGN", "BLCS", "BVAL" };
        private static readonly TimeSpan PositionsPollInterval = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan PositionsStaleAfter = TimeSpan.FromHours(2);
        private static readonly TimeSpan DataStaleAfter = TimeSpan.FromMinutes(15);

        private readonly Dispatcher _ui;
        private readonly AppLog _log = new();
        private readonly LivePositionsService _positions = new();
        private readonly Mx3ExportService _export = new();
        private readonly DataValidator _validator = new();
        private readonly BloombergService _bbg;
        private readonly SharedSettingsService _shared;
        private readonly SchedulerLeaseService _lease;
        private readonly Mx3ImportMonitor _importMonitor;
        private readonly DispatcherTimer _clock;
        private readonly DispatcherTimer _successFadeTimer;
        private readonly SemaphoreSlim _operationLock = new(1, 1);

        // Senaste och föregående hämtning, per valutapar
        private Dictionary<string, List<VolatilityTenor>> _latest = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, List<VolatilityTenor>> _previous = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, (string Atm, string Smile)> _loadedSources = new(StringComparer.OrdinalIgnoreCase);
        private ValidationResult _validation = ValidationResult.Empty;
        private DateTime? _loadedAt;

        private DateTime _positionsFileTime = DateTime.MinValue;
        private DateTime _nextPositionsPoll = DateTime.MinValue;
        private bool _positionsRefreshRunning;

        private DateTime? _attemptSlot;
        private int _attemptCount;
        private DateTime _lastAttemptAt;
        private bool _scheduledRunActive;

        private bool _started;
        private bool _disposed;

        public MainViewModel()
        {
            _ui = Dispatcher.CurrentDispatcher;
            _log.Written += OnLogWritten;

            _bbg = new BloombergService(_log);
            _shared = new SharedSettingsService(_log);
            _importMonitor = new Mx3ImportMonitor(_export.OutputDir, _log);
            _lease = new SchedulerLeaseService(
                Path.Combine(_shared.SettingsDirectory, "scheduler.lock"),
                _log,
                isEligible: () => _bbg.IsConnected && _bbg.HasLiveData);

            foreach (var source in DefaultSources)
                AvailableSources.Add(source);

            LoadDataCommand = new AsyncRelayCommand(
                () => RunExclusiveAsync(() => LoadCoreAsync("manual")), () => !IsBusy, LogCommandError);
            ImportAtmCommand = new AsyncRelayCommand(
                () => RunExclusiveAsync(() => ExportCoreAsync(Mx3FileKind.Atm, "manual")), () => !IsBusy && HasData, LogCommandError);
            ImportSmileCommand = new AsyncRelayCommand(
                () => RunExclusiveAsync(() => ExportCoreAsync(Mx3FileKind.Smile, "manual")), () => !IsBusy && HasData, LogCommandError);
            LoadAndImportCommand = new AsyncRelayCommand(
                () => RunExclusiveAsync(() => LoadAndImportCoreAsync("manual")), () => !IsBusy, LogCommandError);
            RefreshPairsCommand = new AsyncRelayCommand(() => RefreshPairsAsync(manual: true), null, LogCommandError);
            ReconnectCommand = new AsyncRelayCommand(ReconnectAsync, null, LogCommandError);
            OpenLogFolderCommand = new RelayCommand(_ => OpenLogFolder());
            ClearLogCommand = new RelayCommand(_ => LogEntries.Clear());

            _bbg.StateChanged += (_, _) => OnUi(UpdateBloombergStatus);
            _lease.MasterChanged += (_, _) => OnUi(UpdateSchedulerStatus);
            _shared.SettingsChanged += (_, _) => OnUi(ApplySharedSettings);
            _importMonitor.Changed += (_, _) => OnUi(UpdateImportStatus);

            _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _clock.Tick += OnClockTick;

            _successFadeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _successFadeTimer.Tick += (_, _) =>
            {
                ImportJustCompleted = false;
                _successFadeTimer.Stop();
            };
        }

        // =====================================================================
        // Bindbara egenskaper
        // =====================================================================

        public ObservableCollection<CurrencyPairViewModel> CurrencyPairs { get; } = new();
        public ObservableCollection<SurfaceRowViewModel> SurfaceRows { get; } = new();
        public ObservableCollection<LogEntry> LogEntries { get; } = new();
        public ObservableCollection<string> AvailableSources { get; } = new();

        public string UserDisplay { get; } = $"{Environment.UserName} · {Environment.MachineName}";
        public string ScheduleDescription => ImportSchedule.Description;

        public ICommand LoadDataCommand { get; }
        public ICommand ImportAtmCommand { get; }
        public ICommand ImportSmileCommand { get; }
        public ICommand LoadAndImportCommand { get; }
        public ICommand RefreshPairsCommand { get; }
        public ICommand ReconnectCommand { get; }
        public ICommand OpenLogFolderCommand { get; }
        public ICommand ClearLogCommand { get; }

        private string _statusText = "Starting…";
        public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

        // --- Bloomberg ---
        private bool _isConnected;
        public bool IsConnected { get => _isConnected; private set => SetProperty(ref _isConnected, value); }

        private string _bloombergText = "Bloomberg";
        public string BloombergText { get => _bloombergText; private set => SetProperty(ref _bloombergText, value); }

        private string _bloombergDetail = "";
        public string BloombergDetail { get => _bloombergDetail; private set => SetProperty(ref _bloombergDetail, value); }

        private UiLevel _bloombergLevel = UiLevel.Neutral;
        public UiLevel BloombergLevel { get => _bloombergLevel; private set => SetProperty(ref _bloombergLevel, value); }

        private bool _showBloombergBanner;
        public bool ShowBloombergBanner { get => _showBloombergBanner; private set => SetProperty(ref _showBloombergBanner, value); }

        // --- Scheduler ---
        private bool _isMaster;
        public bool IsMaster { get => _isMaster; private set => SetProperty(ref _isMaster, value); }

        private string _schedulerText = "Scheduler";
        public string SchedulerText { get => _schedulerText; private set => SetProperty(ref _schedulerText, value); }

        private string _schedulerDetail = "";
        public string SchedulerDetail { get => _schedulerDetail; private set => SetProperty(ref _schedulerDetail, value); }

        private UiLevel _schedulerLevel = UiLevel.Neutral;
        public UiLevel SchedulerLevel { get => _schedulerLevel; private set => SetProperty(ref _schedulerLevel, value); }

        private string _nextScheduledText = "–";
        public string NextScheduledText { get => _nextScheduledText; private set => SetProperty(ref _nextScheduledText, value); }

        private string _nextScheduledDetail = "";
        public string NextScheduledDetail { get => _nextScheduledDetail; private set => SetProperty(ref _nextScheduledDetail, value); }

        private string _lastScheduledText = "";
        public string LastScheduledText { get => _lastScheduledText; private set => SetProperty(ref _lastScheduledText, value); }

        private UiLevel _lastScheduledLevel = UiLevel.Neutral;
        public UiLevel LastScheduledLevel { get => _lastScheduledLevel; private set => SetProperty(ref _lastScheduledLevel, value); }

        private bool _scheduledImportsEnabled = true;
        /// <summary>Global av/på för de schemalagda importerna – gäller alla användare.</summary>
        public bool ScheduledImportsEnabled
        {
            get => _scheduledImportsEnabled;
            set
            {
                if (!SetProperty(ref _scheduledImportsEnabled, value))
                    return;

                _ = Task.Run(() => _shared.SetScheduledImportEnabled(value));
                _log.Info(value
                    ? "Scheduled imports enabled (applies to all users)"
                    : "Scheduled imports paused (applies to all users)");
                UpdateSchedulerStatus();
            }
        }

        // --- Hämtning / validering ---
        private bool _isBusy;
        public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }

        private bool _isLoading;
        public bool IsLoading { get => _isLoading; private set => SetProperty(ref _isLoading, value); }

        private string _busyText = "";
        public string BusyText { get => _busyText; private set => SetProperty(ref _busyText, value); }

        private bool _hasData;
        public bool HasData { get => _hasData; private set => SetProperty(ref _hasData, value); }

        private string _lastLoadText = "Not loaded";
        public string LastLoadText { get => _lastLoadText; private set => SetProperty(ref _lastLoadText, value); }

        private string _lastLoadDetail = "Press Load data";
        public string LastLoadDetail { get => _lastLoadDetail; private set => SetProperty(ref _lastLoadDetail, value); }

        private UiLevel _lastLoadLevel = UiLevel.Neutral;
        public UiLevel LastLoadLevel { get => _lastLoadLevel; private set => SetProperty(ref _lastLoadLevel, value); }

        private string _validationText = "–";
        public string ValidationText { get => _validationText; private set => SetProperty(ref _validationText, value); }

        private string _validationDetail = "Runs after each load";
        public string ValidationDetail { get => _validationDetail; private set => SetProperty(ref _validationDetail, value); }

        private UiLevel _validationLevel = UiLevel.Neutral;
        public UiLevel ValidationLevel { get => _validationLevel; private set => SetProperty(ref _validationLevel, value); }

        // --- MX3-import ---
        private string _atmImportText = "Idle";
        public string AtmImportText { get => _atmImportText; private set => SetProperty(ref _atmImportText, value); }

        private UiLevel _atmImportLevel = UiLevel.Neutral;
        public UiLevel AtmImportLevel { get => _atmImportLevel; private set => SetProperty(ref _atmImportLevel, value); }

        private string _smileImportText = "Idle";
        public string SmileImportText { get => _smileImportText; private set => SetProperty(ref _smileImportText, value); }

        private UiLevel _smileImportLevel = UiLevel.Neutral;
        public UiLevel SmileImportLevel { get => _smileImportLevel; private set => SetProperty(ref _smileImportLevel, value); }

        private bool _isImporting;
        /// <summary>True medan någon importfil ligger och väntar på MX3 (används av widgeten).</summary>
        public bool IsImporting { get => _isImporting; private set => SetProperty(ref _isImporting, value); }

        private bool _importJustCompleted;
        public bool ImportJustCompleted { get => _importJustCompleted; private set => SetProperty(ref _importJustCompleted, value); }

        // --- Positionsfil ---
        private string _positionsText = "Reading live positions…";
        public string PositionsText { get => _positionsText; private set => SetProperty(ref _positionsText, value); }

        private bool _positionsStale;
        public bool PositionsStale { get => _positionsStale; private set => SetProperty(ref _positionsStale, value); }

        // --- Valt valutapar ---
        private CurrencyPairViewModel? _selectedPair;
        public CurrencyPairViewModel? SelectedPair
        {
            get => _selectedPair;
            set
            {
                if (SetProperty(ref _selectedPair, value))
                    RefreshSelectedSurface();
            }
        }

        private IReadOnlyList<TermPoint> _chartPoints = Array.Empty<TermPoint>();
        public IReadOnlyList<TermPoint> ChartPoints { get => _chartPoints; private set => SetProperty(ref _chartPoints, value); }

        private bool _hasSelectedData;
        public bool HasSelectedData { get => _hasSelectedData; private set => SetProperty(ref _hasSelectedData, value); }

        private string _selectedSubtitle = "";
        public string SelectedSubtitle { get => _selectedSubtitle; private set => SetProperty(ref _selectedSubtitle, value); }

        private string _selectedEmptyText = "";
        public string SelectedEmptyText { get => _selectedEmptyText; private set => SetProperty(ref _selectedEmptyText, value); }

        // =====================================================================
        // Uppstart
        // =====================================================================

        public async Task StartAsync()
        {
            if (_started)
                return;
            _started = true;

            _log.Info($"FX Volatility Import started ({UserDisplay})");

            try
            {
                await Task.Run(() => _shared.Start());
                foreach (var source in _shared.KnownSources())
                    EnsureSource(source);

                _scheduledImportsEnabled = _shared.ScheduledImportEnabled;
                OnPropertyChanged(nameof(ScheduledImportsEnabled));

                await RefreshPairsAsync(manual: false);
            }
            catch (Exception ex)
            {
                _log.Error($"Startup problem: {ex.Message}");
            }

            _bbg.Start();
            _lease.Start();
            _importMonitor.Start();
            _clock.Start();

            UpdateBloombergStatus();
            UpdateSchedulerStatus();
            UpdateImportStatus();
        }

        // =====================================================================
        // Klocka: status, positionsfil och schemaläggning
        // =====================================================================

        private void OnClockTick(object? sender, EventArgs e)
        {
            var now = DateTime.Now;

            UpdateBloombergStatus();
            UpdateSchedulerStatus();
            UpdateImportStatus();
            UpdatePositionsText();

            if (now >= _nextPositionsPoll)
            {
                _nextPositionsPoll = now + PositionsPollInterval;
                _ = CheckPositionsFileAsync();
            }

            CheckSchedule(now);
        }

        /// <summary>
        /// Körs varje sekund. Bara master kör schemalagda importer. En slot körs om den har passerat,
        /// ligger inom grace-fönstret och inte redan importerats utan fel (enligt scheduler.json, som delas
        /// mellan alla – så en ny master kör inte om en slot som den förra mastern redan klarat).
        /// </summary>
        private void CheckSchedule(DateTime now)
        {
            if (!_lease.IsMaster || !ScheduledImportsEnabled || _scheduledRunActive)
                return;

            var slot = ImportSchedule.LatestSlot(now);
            if (slot == null || now - slot.Value > ImportSchedule.Grace)
                return;

            var lastOk = _lease.LastSuccessfulSlot;
            if (lastOk.HasValue && lastOk.Value >= slot.Value)
                return;

            if (_attemptSlot == slot)
            {
                if (_attemptCount >= ImportSchedule.MaxAttemptsPerSlot)
                    return;
                if (now - _lastAttemptAt < ImportSchedule.RetryDelay)
                    return;
            }
            else
            {
                _attemptSlot = slot;
                _attemptCount = 0;
            }

            _attemptCount++;
            _lastAttemptAt = now;
            _ = RunScheduledImportAsync(slot.Value, _attemptCount);
        }

        private async Task RunScheduledImportAsync(DateTime slot, int attempt)
        {
            _scheduledRunActive = true;
            try
            {
                if (!await _operationLock.WaitAsync(TimeSpan.FromMinutes(2)))
                {
                    _log.Warning($"Scheduled import {slot:HH:mm} postponed – another load/import is still running");
                    return;
                }

                IsBusy = true;
                try
                {
                    _log.Info($"Scheduled import {slot:HH:mm} started" + (attempt > 1 ? $" (attempt {attempt})" : ""));
                    await RefreshPairsAsync(manual: false);

                    var (ok, summary) = await LoadAndImportCoreAsync($"scheduled {slot:HH:mm}");
                    await Task.Run(() => _lease.RecordRun(slot, ok, summary));

                    if (ok)
                        _log.Success($"Scheduled import {slot:HH:mm} done – {summary}");
                    else if (attempt < ImportSchedule.MaxAttemptsPerSlot)
                        _log.Error($"Scheduled import {slot:HH:mm} failed – {summary}. Retrying in {ImportSchedule.RetryDelay.TotalMinutes:0} min");
                    else
                        _log.Error($"Scheduled import {slot:HH:mm} failed – {summary}. No more retries for this slot");
                }
                finally
                {
                    IsBusy = false;
                    _operationLock.Release();
                }
            }
            catch (Exception ex)
            {
                _log.Error($"Scheduled import {slot:HH:mm} crashed: {ex.Message}");
            }
            finally
            {
                _scheduledRunActive = false;
                UpdateSchedulerStatus();
            }
        }

        // =====================================================================
        // Hämtning och export
        // =====================================================================

        private async Task RunExclusiveAsync(Func<Task> action)
        {
            if (!await _operationLock.WaitAsync(0))
            {
                _log.Info("Another load/import is running – please wait");
                return;
            }

            IsBusy = true;
            try
            {
                await action();
            }
            finally
            {
                IsBusy = false;
                _operationLock.Release();
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private async Task<bool> LoadCoreAsync(string trigger)
        {
            var included = CurrencyPairs.Where(p => p.IsLive).Select(p => p.ToConfig()).ToList();
            if (included.Count == 0)
            {
                _log.Warning("No currency pairs are included – nothing to load");
                return false;
            }

            IsLoading = true;
            BusyText = $"Loading {included.Count} pairs from Bloomberg…";
            try
            {
                var result = await _bbg.LoadVolatilityDataAsync(included);
                var validation = _validator.Validate(result.Data);
                ApplyLoadResult(result, validation, included);

                var excluded = validation.PairsWithErrors.Count;
                var message = $"Loaded {_latest.Count} pairs ({result.Data.Count} points) from Bloomberg [{trigger}]";
                if (excluded > 0)
                    _log.Warning($"{message} – {excluded} pair(s) failed validation and will not be imported");
                else if (validation.WarningCount > 0)
                    _log.Info($"{message} – {validation.WarningCount} warning(s)");
                else
                    _log.Success(message);

                if (result.BloombergErrors.Count > 0)
                {
                    var sample = string.Join("; ", result.BloombergErrors.Take(3));
                    var more = result.BloombergErrors.Count > 3 ? $" (+{result.BloombergErrors.Count - 3} more)" : "";
                    _log.Warning($"Bloomberg reported {result.BloombergErrors.Count} error(s): {sample}{more}");
                }

                return _latest.Count > 0;
            }
            catch (Exception ex)
            {
                _log.Error($"Load failed: {ex.Message}");
                return false;
            }
            finally
            {
                IsLoading = false;
                BusyText = "";
                UpdateBloombergStatus();
            }
        }

        private void ApplyLoadResult(VolatilityLoadResult result, ValidationResult validation, List<CurrencyPairConfig> included)
        {
            _previous = _latest;
            _latest = result.Data
                .GroupBy(d => d.CurrencyPair, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            _loadedSources = included.ToDictionary(
                c => c.CurrencyPair, c => (c.AtmSource, c.SmileSource), StringComparer.OrdinalIgnoreCase);
            _validation = validation;
            _loadedAt = result.LoadedAt;
            HasData = _latest.Count > 0;

            foreach (var pair in CurrencyPairs)
            {
                if (!_latest.ContainsKey(pair.CurrencyPair))
                {
                    pair.SetStatus(PairStatus.NotLoaded, pair.IsLive ? "Not loaded" : "Not included in load/import");
                    continue;
                }

                var issues = validation.For(pair.CurrencyPair);
                var errors = issues.Where(i => i.Severity == IssueSeverity.Error).ToList();
                if (errors.Count > 0)
                    pair.SetStatus(PairStatus.Error, "Excluded from import:" + Environment.NewLine + Describe(errors));
                else if (issues.Count > 0)
                    pair.SetStatus(PairStatus.Warning, Describe(issues));
                else
                    pair.SetStatus(PairStatus.Ok, $"Loaded {result.LoadedAt:HH:mm:ss} – all checks passed");
            }

            LastLoadText = result.LoadedAt.ToString("HH:mm:ss");
            LastLoadDetail = $"{_latest.Count} pairs · {result.Data.Count} points";
            LastLoadLevel = UiLevel.Good;

            var excludedPairs = validation.PairsWithErrors.OrderBy(p => p).ToList();
            if (excludedPairs.Count > 0)
            {
                ValidationText = $"{excludedPairs.Count} excluded";
                ValidationDetail = string.Join(", ", excludedPairs.Select(CurrencyPairMapper.ToMx3Format));
                ValidationLevel = UiLevel.Error;
            }
            else if (validation.WarningCount > 0)
            {
                ValidationText = $"{validation.WarningCount} warning{(validation.WarningCount == 1 ? "" : "s")}";
                ValidationDetail = "All pairs will be imported";
                ValidationLevel = UiLevel.Warning;
            }
            else
            {
                ValidationText = "All passed";
                ValidationDetail = "All pairs will be imported";
                ValidationLevel = UiLevel.Good;
            }

            RefreshSelectedSurface();
        }

        private static string Describe(IEnumerable<ValidationIssue> issues)
        {
            var list = issues.ToList();
            var lines = list.Take(6).Select(i => $"{i.Tenor}: {i.Message}");
            var more = list.Count > 6 ? $"{Environment.NewLine}… and {list.Count - 6} more" : "";
            return string.Join(Environment.NewLine, lines) + more;
        }

        private async Task<bool> ExportCoreAsync(Mx3FileKind kind, string trigger)
        {
            var label = Mx3ImportMonitor.Label(kind);

            // Bara par som ingår just nu och klarade valideringen exporteras
            var includedNow = CurrencyPairs.Where(p => p.IsLive).Select(p => p.CurrencyPair)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var excluded = _latest.Keys
                .Where(k => includedNow.Contains(k) && _validation.PairsWithErrors.Contains(k))
                .OrderBy(k => k)
                .ToList();
            var exportable = _latest
                .Where(kv => includedNow.Contains(kv.Key) && !_validation.PairsWithErrors.Contains(kv.Key))
                .OrderBy(kv => kv.Key)
                .SelectMany(kv => kv.Value)
                .ToList();

            if (exportable.Count == 0)
            {
                _log.Error($"{label} not written – no valid data" +
                           (excluded.Count > 0 ? $" ({excluded.Count} pair(s) failed validation)" : " (load data first)"));
                return false;
            }

            if (_loadedAt.HasValue && DateTime.Now - _loadedAt.Value > DataStaleAfter)
                _log.Warning($"{label}: data was loaded at {_loadedAt:HH:mm} – consider reloading before importing");

            try
            {
                await Task.Run(() =>
                {
                    if (kind == Mx3FileKind.Atm)
                        _export.ExportAtm(exportable);
                    else
                        _export.ExportSmile(exportable);
                });

                _importMonitor.NotifyWritten(kind);
                var pairCount = exportable.Select(d => d.CurrencyPair).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                var excludedText = excluded.Count > 0
                    ? $", excluded: {string.Join(", ", excluded.Select(CurrencyPairMapper.ToMx3Format))}"
                    : "";
                _log.Success($"{label} file written for MX3 ({pairCount} pairs{excludedText}) [{trigger}]");
                UpdateImportStatus();
                return true;
            }
            catch (Exception ex)
            {
                _log.Error($"{label} export failed: {ex.Message}");
                return false;
            }
        }

        private async Task<(bool Ok, string Summary)> LoadAndImportCoreAsync(string trigger)
        {
            if (!await LoadCoreAsync(trigger))
                return (false, "load from Bloomberg failed");

            var atmOk = await ExportCoreAsync(Mx3FileKind.Atm, trigger);
            var smileOk = await ExportCoreAsync(Mx3FileKind.Smile, trigger);

            var imported = _latest.Keys.Count(k => !_validation.PairsWithErrors.Contains(k) &&
                                                   CurrencyPairs.Any(p => p.IsLive && p.CurrencyPair.Equals(k, StringComparison.OrdinalIgnoreCase)));
            var excluded = _validation.PairsWithErrors.Count;
            var summary = $"{imported} pairs" + (excluded > 0 ? $", {excluded} excluded" : "");

            if (atmOk && smileOk)
                return (true, summary);

            return (false, $"{(atmOk ? "" : "ATM ")}{(smileOk ? "" : "Smile ")}export failed".Trim());
        }

        // =====================================================================
        // Valutapar och delade inställningar
        // =====================================================================

        private async Task CheckPositionsFileAsync()
        {
            var fileTime = await Task.Run(_positions.GetFileLastModified);
            if (fileTime != DateTime.MinValue && fileTime != _positionsFileTime)
                await RefreshPairsAsync(manual: false);
        }

        private async Task RefreshPairsAsync(bool manual)
        {
            if (_positionsRefreshRunning)
                return;
            _positionsRefreshRunning = true;

            try
            {
                LivePositionsSnapshot snapshot;
                try
                {
                    snapshot = await Task.Run(_positions.Read);
                }
                catch (Exception ex)
                {
                    _log.Warning($"Could not read live positions file ({ex.Message}) – keeping the current pair list");
                    return;
                }

                // En tom fil som just skrivits är troligen mitt i en skrivning – vänta en minut innan vi litar på den
                if (snapshot.Pairs.Count == 0 && CurrencyPairs.Count > 0 && DateTime.Now - snapshot.FileTime < TimeSpan.FromMinutes(1))
                {
                    _log.Warning("Live positions file has no option positions right now (may be mid-write) – keeping the current list");
                    return;
                }

                _positionsFileTime = snapshot.FileTime;

                var removed = CurrencyPairs
                    .Where(p => !snapshot.Pairs.Contains(p.CurrencyPair, StringComparer.OrdinalIgnoreCase))
                    .ToList();
                foreach (var pair in removed)
                {
                    pair.UserEdited -= OnPairEdited;
                    CurrencyPairs.Remove(pair);
                }

                var added = new List<string>();
                foreach (var name in snapshot.Pairs)
                {
                    if (CurrencyPairs.Any(p => p.CurrencyPair.Equals(name, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    // Source hämtas från den delade filen – den minns valet även om paret varit borta en tid
                    var shared = _shared.GetPair(name);
                    if (shared != null)
                    {
                        EnsureSource(shared.AtmSource);
                        EnsureSource(shared.SmileSource);
                    }

                    var vm = new CurrencyPairViewModel(name, shared);
                    vm.UserEdited += OnPairEdited;
                    InsertSorted(vm);
                    added.Add(name);
                }

                if (SelectedPair == null || !CurrencyPairs.Contains(SelectedPair))
                    SelectedPair = CurrencyPairs.FirstOrDefault();

                if (manual || added.Count > 0 || removed.Count > 0)
                {
                    var changes = new List<string>();
                    if (added.Count > 0) changes.Add("added " + string.Join(", ", added.Select(CurrencyPairMapper.ToMx3Format)));
                    if (removed.Count > 0) changes.Add("removed " + string.Join(", ", removed.Select(p => p.DisplayName)));
                    _log.Info($"Live positions ({snapshot.FileTime:HH:mm}): {snapshot.Pairs.Count} pairs with options" +
                              (changes.Count > 0 ? " – " + string.Join("; ", changes) : ""));
                }

                UpdatePositionsText();
            }
            finally
            {
                _positionsRefreshRunning = false;
            }
        }

        private void InsertSorted(CurrencyPairViewModel vm)
        {
            var index = 0;
            while (index < CurrencyPairs.Count &&
                   string.Compare(CurrencyPairs[index].CurrencyPair, vm.CurrencyPair, StringComparison.OrdinalIgnoreCase) < 0)
                index++;
            CurrencyPairs.Insert(index, vm);
        }

        private void EnsureSource(string source)
        {
            if (!string.IsNullOrWhiteSpace(source) &&
                !AvailableSources.Contains(source, StringComparer.OrdinalIgnoreCase))
                AvailableSources.Add(source.ToUpperInvariant());
        }

        /// <summary>Användaren ändrade source eller "ingår" – spara i den delade filen så alla (även master) följer med.</summary>
        private void OnPairEdited(object? sender, EventArgs e)
        {
            if (sender is not CurrencyPairViewModel vm)
                return;

            if (_latest.ContainsKey(vm.CurrencyPair) || vm.Status != PairStatus.NotLoaded)
                vm.MarkStale();

            var (pair, atm, smile, live) = (vm.CurrencyPair, vm.AtmSource, vm.SmileSource, vm.IsLive);
            _ = Task.Run(() => _shared.UpdatePair(pair, atm, smile, live));
            _log.Info($"{vm.DisplayName}: ATM {atm} · Smile {smile}{(live ? "" : " · not included")} – saved for all users");

            if (ReferenceEquals(vm, SelectedPair))
                RefreshSelectedSurface();
        }

        /// <summary>En annan användare har ändrat den delade filen.</summary>
        private void ApplySharedSettings()
        {
            foreach (var source in _shared.KnownSources())
                EnsureSource(source);

            foreach (var vm in CurrencyPairs)
            {
                var shared = _shared.GetPair(vm.CurrencyPair);
                if (shared != null && vm.ApplyShared(shared))
                {
                    vm.MarkStale();
                    _log.Info($"{vm.DisplayName} changed by {shared.ModifiedBy}: ATM {shared.AtmSource} · Smile {shared.SmileSource}" +
                              (shared.IsLive ? "" : " · not included"));
                }
            }

            var enabled = _shared.ScheduledImportEnabled;
            if (enabled != _scheduledImportsEnabled)
            {
                _scheduledImportsEnabled = enabled;
                OnPropertyChanged(nameof(ScheduledImportsEnabled));
                _log.Info(enabled ? "Scheduled imports were enabled by another user" : "Scheduled imports were paused by another user");
            }

            RefreshSelectedSurface();
            UpdateSchedulerStatus();
        }

        private void RefreshSelectedSurface()
        {
            SurfaceRows.Clear();
            var pair = SelectedPair;

            if (pair == null || !_latest.TryGetValue(pair.CurrencyPair, out var rows))
            {
                ChartPoints = Array.Empty<TermPoint>();
                HasSelectedData = false;
                SelectedSubtitle = pair == null ? "" : $"ATM {pair.AtmSource} · Smile {pair.SmileSource}";
                SelectedEmptyText = pair == null
                    ? "No currency pair selected"
                    : pair.IsLive
                        ? "Not loaded yet – press Load data"
                        : "Not included – switch it on to load and import it";
                return;
            }

            _previous.TryGetValue(pair.CurrencyPair, out var previousRows);
            var issues = _validation.For(pair.CurrencyPair);

            foreach (var tenor in rows)
            {
                var previous = previousRows?.FirstOrDefault(p => p.Tenor == tenor.Tenor);
                var tenorIssues = issues.Where(i => i.Tenor == tenor.Tenor).ToList();
                SurfaceRows.Add(new SurfaceRowViewModel(tenor, previous, tenorIssues));
            }

            ChartPoints = rows.Select(r => new TermPoint(r.Tenor, r.AtmBid, r.AtmAsk)).ToList();
            HasSelectedData = true;

            var sources = _loadedSources.TryGetValue(pair.CurrencyPair, out var s) ? s : (pair.AtmSource, pair.SmileSource);
            SelectedSubtitle = $"Loaded {_loadedAt:HH:mm:ss} · ATM {sources.Item1} · Smile {sources.Item2}" +
                               (previousRows != null ? " · Δ vs previous load" : "");
            SelectedEmptyText = "";
        }

        // =====================================================================
        // Statusuppdatering
        // =====================================================================

        private void UpdateBloombergStatus()
        {
            var state = _bbg.State;
            IsConnected = state == BloombergState.Connected;

            switch (state)
            {
                case BloombergState.Connected:
                    var since = _bbg.ConnectedSince;
                    var heartbeat = _bbg.LastHeartbeat;
                    if (_bbg.HasLiveData)
                    {
                        BloombergText = "Bloomberg";
                        BloombergLevel = UiLevel.Good;
                    }
                    else
                    {
                        BloombergText = heartbeat.HasValue ? "Bloomberg · no data" : "Bloomberg · checking";
                        BloombergLevel = heartbeat.HasValue ? UiLevel.Warning : UiLevel.Info;
                    }
                    BloombergDetail = $"Connected since {since:HH:mm:ss}" +
                                      (heartbeat.HasValue ? $" · last check {heartbeat:HH:mm:ss}" : "") +
                                      (_bbg.HasLiveData ? "" : " · no data returned – is the Terminal logged in?");
                    break;

                case BloombergState.Connecting:
                    BloombergText = "Connecting…";
                    BloombergLevel = UiLevel.Info;
                    BloombergDetail = "Starting Bloomberg session";
                    break;

                default:
                    var next = _bbg.NextReconnectAttempt;
                    var seconds = next.HasValue ? (int)Math.Ceiling((next.Value - DateTime.Now).TotalSeconds) : 0;
                    BloombergText = seconds > 0 ? $"Reconnecting in {seconds}s" : "Reconnecting…";
                    BloombergLevel = UiLevel.Error;
                    BloombergDetail = $"Disconnected: {_bbg.LastError ?? "not connected yet"}" +
                                      (_bbg.FailedAttempts > 0 ? $" · {_bbg.FailedAttempts} failed attempt(s)" : "");
                    break;
            }

            ShowBloombergBanner = _started && state == BloombergState.Disconnected;
        }

        private void UpdateSchedulerStatus()
        {
            var now = DateTime.Now;
            IsMaster = _lease.IsMaster;
            var holder = _lease.Holder;
            var holderAlive = _lease.IsHolderAlive;

            if (IsMaster)
            {
                SchedulerText = "Master · this PC";
                SchedulerLevel = UiLevel.Good;
                SchedulerDetail = "This instance runs the scheduled imports. If it closes, another user's app takes over automatically.";
            }
            else if (holder != null && holderAlive)
            {
                SchedulerText = $"Master · {holder.User}";
                SchedulerLevel = UiLevel.Info;
                SchedulerDetail = $"Scheduled imports run on {holder.User} @ {holder.Machine} (heartbeat {holder.Heartbeat:HH:mm:ss}). " +
                                  "This PC takes over automatically if that app closes.";
            }
            else
            {
                SchedulerText = "No master";
                SchedulerLevel = UiLevel.Warning;
                SchedulerDetail = _bbg.IsConnected && _bbg.HasLiveData
                    ? "Waiting for the master lock – this PC will take over within a few seconds."
                    : "No running instance with a working Bloomberg connection. Scheduled imports are not running.";
            }

            var next = ImportSchedule.NextSlot(now);
            NextScheduledText = next.Date == now.Date
                ? next.ToString("HH:mm")
                : next.ToString("ddd HH:mm", CultureInfo.InvariantCulture);

            NextScheduledDetail = !ScheduledImportsEnabled
                ? "Paused for all users"
                : IsMaster
                    ? "Runs on this PC"
                    : holder != null && holderAlive
                        ? $"Runs on {holder.User}'s PC"
                        : "No master running";

            if (holder?.LastRunAt is DateTime lastRun)
            {
                var day = lastRun.Date == now.Date ? "" : lastRun.ToString("ddd ", CultureInfo.InvariantCulture);
                LastScheduledText = holder.LastRunOk
                    ? $"Last {day}{lastRun:HH:mm} ✓ {holder.LastRunSummary}"
                    : $"Last {day}{lastRun:HH:mm} ✗ {holder.LastRunSummary}";
                LastScheduledLevel = holder.LastRunOk ? UiLevel.Good : UiLevel.Error;
            }
            else
            {
                LastScheduledText = "No scheduled run yet";
                LastScheduledLevel = UiLevel.Neutral;
            }
        }

        private void UpdateImportStatus()
        {
            var atm = _importMonitor.GetStatus(Mx3FileKind.Atm);
            var smile = _importMonitor.GetStatus(Mx3FileKind.Smile);

            (AtmImportText, AtmImportLevel) = DescribeImport(atm);
            (SmileImportText, SmileImportLevel) = DescribeImport(smile);

            var importing = atm.Pending || smile.Pending;
            if (IsImporting && !importing)
            {
                ImportJustCompleted = true;
                _successFadeTimer.Stop();
                _successFadeTimer.Start();
            }
            IsImporting = importing;
        }

        private static (string Text, UiLevel Level) DescribeImport(Mx3FileStatus status)
        {
            if (status.Pending && status.PendingSince is DateTime since)
            {
                var elapsed = DateTime.Now - since;
                return ($"Importing · {Mx3ImportMonitor.FormatDuration(elapsed)}",
                        elapsed > TimeSpan.FromMinutes(10) ? UiLevel.Warning : UiLevel.Info);
            }

            if (status.LastCompleted is DateTime done)
                return ($"Done {done:HH:mm:ss}", UiLevel.Good);

            return ("Idle", UiLevel.Neutral);
        }

        private void UpdatePositionsText()
        {
            if (_positionsFileTime == DateTime.MinValue)
            {
                PositionsText = "Live positions file not read yet";
                PositionsStale = true;
                return;
            }

            var age = DateTime.Now - _positionsFileTime;
            var stale = age > PositionsStaleAfter && ImportSchedule.IsTradingDay(DateTime.Now);
            var when = _positionsFileTime.Date == DateTime.Today
                ? _positionsFileTime.ToString("HH:mm")
                : _positionsFileTime.ToString("yyyy-MM-dd HH:mm");

            PositionsText = stale
                ? $"Positions file {when} – not updated for {(int)age.TotalHours}h"
                : $"Live option positions · file {when}";
            PositionsStale = stale;
        }

        // =====================================================================
        // Övrigt
        // =====================================================================

        private async Task ReconnectAsync()
        {
            _log.Info("Reconnecting to Bloomberg…");
            await _bbg.ConnectAsync(force: true);
            UpdateBloombergStatus();
        }

        private void OpenLogFolder()
        {
            try
            {
                Directory.CreateDirectory(_log.LogDirectory);
                Process.Start(new ProcessStartInfo("explorer.exe", _log.LogDirectory) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _log.Error($"Could not open log folder: {ex.Message}");
            }
        }

        private void OnLogWritten(object? sender, LogEntry entry)
        {
            OnUi(() =>
            {
                LogEntries.Insert(0, entry);
                while (LogEntries.Count > MaxLogEntries)
                    LogEntries.RemoveAt(LogEntries.Count - 1);
                StatusText = entry.Message;
            });
        }

        private void LogCommandError(Exception ex) => _log.Error($"Unexpected error: {ex.Message}");

        private void OnUi(Action action)
        {
            if (_disposed)
                return;

            if (_ui.CheckAccess())
                action();
            else
                _ui.InvokeAsync(action);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            _clock.Stop();
            _successFadeTimer.Stop();

            foreach (var pair in CurrencyPairs)
                pair.UserEdited -= OnPairEdited;

            _log.Info("FX Volatility Import closing");
            _lease.Dispose();       // släpper master-rollen direkt så någon annan tar över
            _importMonitor.Dispose();
            _shared.Dispose();
            _bbg.Dispose();
        }
    }
}