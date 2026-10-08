// ViewModels/AsyncRelayCommand.cs
using System.Windows.Input;

namespace FxVolatilityImport.ViewModels
{
    /// <summary>
    /// Kommando för asynkrona operationer. Är avstängt medan det körs, så man inte kan dubbelklicka igång två hämtningar.
    /// </summary>
    public sealed class AsyncRelayCommand : ICommand
    {
        private readonly Func<Task> _execute;
        private readonly Func<bool>? _canExecute;
        private readonly Action<Exception>? _onError;
        private bool _running;

        public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null, Action<Exception>? onError = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
            _onError = onError;
        }

        public event EventHandler? CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }

        public bool CanExecute(object? parameter) => !_running && (_canExecute?.Invoke() ?? true);

        public async void Execute(object? parameter)
        {
            if (!CanExecute(parameter))
                return;

            _running = true;
            CommandManager.InvalidateRequerySuggested();
            try
            {
                await _execute();
            }
            catch (Exception ex)
            {
                _onError?.Invoke(ex);
            }
            finally
            {
                _running = false;
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }
}