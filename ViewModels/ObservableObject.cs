// ViewModels/ObservableObject.cs
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FxVolatilityImport.ViewModels
{
    public abstract class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return false;

            field = value;
            OnPropertyChanged(name);
            return true;
        }
    }

    /// <summary>Färgnivå för statusindikatorer i UI:t (mappas till färger i Theme.xaml).</summary>
    public enum UiLevel { Neutral, Good, Info, Warning, Error }
}