// ViewModels/CurrencyPairViewModel.cs
using FxVolatilityImport.Models;
using FxVolatilityImport.Services;

namespace FxVolatilityImport.ViewModels
{
    public enum PairStatus { NotLoaded, Ok, Warning, Error, Stale }

    public sealed class CurrencyPairViewModel : ObservableObject
    {
        private string _atmSource;
        private string _smileSource;
        private bool _isLive;
        private bool _applyingShared;
        private PairStatus _status = PairStatus.NotLoaded;
        private string _statusTooltip = "Not loaded yet";

        public CurrencyPairViewModel(string currencyPair, PairSourceConfig? shared)
        {
            CurrencyPair = currencyPair;
            _atmSource = Normalize(shared?.AtmSource) ?? "BGN";
            _smileSource = Normalize(shared?.SmileSource) ?? "BGN";
            _isLive = shared?.IsLive ?? true;
        }

        /// <summary>"blcs " → "BLCS" så att dropdown-listan alltid hittar värdet.</summary>
        private static string? Normalize(string? source)
            => string.IsNullOrWhiteSpace(source) ? null : source.Trim().ToUpperInvariant();

        /// <summary>
        /// Användaren har ändrat source eller IsLive i UI:t (inte när ändringen kommer från en annan användare).
        /// </summary>
        public event EventHandler? UserEdited;

        public string CurrencyPair { get; }
        public string DisplayName => CurrencyPairMapper.ToMx3Format(CurrencyPair);
        public string BloombergPair => CurrencyPairMapper.ToBloombergPair(CurrencyPair);
        public bool IsInverted => CurrencyPairMapper.IsInverted(CurrencyPair);

        public string AtmSource
        {
            get => _atmSource;
            set
            {
                // En ComboBox kan skicka null när listan byggs om – det är aldrig ett riktigt val
                var normalized = Normalize(value);
                if (normalized == null)
                    return;
                if (SetProperty(ref _atmSource, normalized))
                    RaiseUserEdited();
            }
        }

        public string SmileSource
        {
            get => _smileSource;
            set
            {
                var normalized = Normalize(value);
                if (normalized == null)
                    return;
                if (SetProperty(ref _smileSource, normalized))
                    RaiseUserEdited();
            }
        }

        public bool IsLive
        {
            get => _isLive;
            set
            {
                if (SetProperty(ref _isLive, value))
                {
                    OnPropertyChanged(nameof(StatusShortText));
                    RaiseUserEdited();
                }
            }
        }

        public PairStatus Status
        {
            get => _status;
            private set
            {
                if (SetProperty(ref _status, value))
                {
                    OnPropertyChanged(nameof(StatusLevel));
                    OnPropertyChanged(nameof(StatusShortText));
                }
            }
        }

        public string StatusTooltip
        {
            get => _statusTooltip;
            private set => SetProperty(ref _statusTooltip, value);
        }

        public UiLevel StatusLevel => Status switch
        {
            PairStatus.Ok => UiLevel.Good,
            PairStatus.Warning => UiLevel.Warning,
            PairStatus.Error => UiLevel.Error,
            PairStatus.Stale => UiLevel.Info,
            _ => UiLevel.Neutral
        };

        public string StatusShortText => Status switch
        {
            PairStatus.Ok => "OK",
            PairStatus.Warning => "Warnings",
            PairStatus.Error => "Excluded",
            PairStatus.Stale => "Reload needed",
            _ => IsLive ? "Not loaded" : "Not included"
        };

        public void SetStatus(PairStatus status, string tooltip)
        {
            Status = status;
            StatusTooltip = tooltip;
        }

        public void MarkStale()
            => SetStatus(PairStatus.Stale, "Settings changed since the last load – press Load data to refresh");

        /// <summary>Tar in en ändring som en annan användare gjort. Returnerar true om något ändrades.</summary>
        public bool ApplyShared(PairSourceConfig shared)
        {
            _applyingShared = true;
            try
            {
                var changed = false;
                if (!string.Equals(_atmSource, shared.AtmSource, StringComparison.OrdinalIgnoreCase))
                {
                    AtmSource = shared.AtmSource;
                    changed = true;
                }
                if (!string.Equals(_smileSource, shared.SmileSource, StringComparison.OrdinalIgnoreCase))
                {
                    SmileSource = shared.SmileSource;
                    changed = true;
                }
                if (_isLive != shared.IsLive)
                {
                    IsLive = shared.IsLive;
                    changed = true;
                }
                return changed;
            }
            finally
            {
                _applyingShared = false;
            }
        }

        public CurrencyPairConfig ToConfig() => new()
        {
            CurrencyPair = CurrencyPair,
            AtmSource = AtmSource,
            SmileSource = SmileSource,
            IsLive = IsLive
        };

        private void RaiseUserEdited()
        {
            if (!_applyingShared)
                UserEdited?.Invoke(this, EventArgs.Empty);
        }
    }
}