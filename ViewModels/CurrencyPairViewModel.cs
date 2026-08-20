using System.ComponentModel;
using System.Runtime.CompilerServices;

public class CurrencyPairViewModel : INotifyPropertyChanged
{
    private string _currencyPair = "";
    private string _atmSource = "BGN";
    private string _smileSource = "BGN";
    private bool _isLive = true;

    public event Action<CurrencyPairViewModel>? SourceChanged;

    public string CurrencyPair
    {
        get => _currencyPair;
        set { _currencyPair = value; OnPropertyChanged(); }
    }

    public string AtmSource
    {
        get => _atmSource;
        set
        {
            if (_atmSource != value)
            {
                _atmSource = value;
                OnPropertyChanged();
                SourceChanged?.Invoke(this);
            }
        }
    }

    public string SmileSource
    {
        get => _smileSource;
        set
        {
            if (_smileSource != value)
            {
                _smileSource = value;
                OnPropertyChanged();
                SourceChanged?.Invoke(this);
            }
        }
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