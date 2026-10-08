// Services/AppLog.cs
using System.IO;

namespace FxVolatilityImport.Services
{
    public enum LogLevel { Info, Success, Warning, Error }

    public sealed record LogEntry(DateTime Time, LogLevel Level, string Message);

    /// <summary>
    /// Enkel trådsäker logg: skriver till en dagsfil under %LOCALAPPDATA%\FxVolatilityImport\logs
    /// och notifierar UI:t (aktivitetslistan) via Written.
    /// </summary>
    public sealed class AppLog
    {
        private readonly object _fileLock = new();

        public AppLog()
        {
            LogDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FxVolatilityImport",
                "logs");
        }

        public string LogDirectory { get; }

        /// <summary>Anropas på den tråd som loggade. Prenumeranter måste själva marshalla till UI-tråden.</summary>
        public event EventHandler<LogEntry>? Written;

        public void Info(string message) => Write(LogLevel.Info, message);
        public void Success(string message) => Write(LogLevel.Success, message);
        public void Warning(string message) => Write(LogLevel.Warning, message);
        public void Error(string message) => Write(LogLevel.Error, message);

        public void Write(LogLevel level, string message)
        {
            var entry = new LogEntry(DateTime.Now, level, message);

            try
            {
                lock (_fileLock)
                {
                    Directory.CreateDirectory(LogDirectory);
                    File.AppendAllText(
                        Path.Combine(LogDirectory, $"{entry.Time:yyyyMMdd}.log"),
                        $"{entry.Time:HH:mm:ss.fff} [{level,-7}] {message}{Environment.NewLine}");
                }
            }
            catch
            {
                // Loggning får aldrig fälla applikationen
            }

            Written?.Invoke(this, entry);
        }
    }
}