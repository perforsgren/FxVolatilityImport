// ViewModels/CurrencyPairViewModel.cs
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FxVolatilityImport.ViewModels
{
    public class CurrencyPairViewModel : INotifyPropertyChanged
    {
        private string _currencyPair = "";
        private string _atmSource = "BGN";
        private string _smileSource = "BGN";
        private bool _isLive = true;

        public string CurrencyPair
        {
            get => _currencyPair;
            set { _currencyPair = value; OnPropertyChanged(); }
        }

        public string AtmSource
        {
            get => _atmSource;
            set { _atmSource = value; OnPropertyChanged(); }
        }

        public string SmileSource
        {
            get => _smileSource;
            set { _smileSource = value; OnPropertyChanged(); }
        }

        public bool IsLive
        {
            get => _isLive;
            set { _isLive = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
