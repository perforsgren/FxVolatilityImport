// Services/Mx3ImportMonitor.cs
using System.IO;

namespace FxVolatilityImport.Services
{
    public enum Mx3FileKind { Atm, Smile }

    public sealed record Mx3FileStatus(
        Mx3FileKind Kind,
        bool Pending,
        DateTime? PendingSince,
        DateTime? LastCompleted,
        TimeSpan? LastDuration);

    /// <summary>
    /// Följer importfilerna i MX3-katalogen: finns filen har MX3 inte importerat den än,
    /// försvinner den är importen klar (samma logik som legacy-appen).
    ///
    /// Pollar var 2:a sekund i stället för FileSystemWatcher (opålitlig på nätverksdiskar).
    /// Ser även filer som andra användare eller master har skrivit, så alla ser samma importstatus.
    /// </summary>
    public sealed class Mx3ImportMonitor : IDisposable
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan StuckAfter = TimeSpan.FromMinutes(10);

        private readonly string _outputDir;
        private readonly AppLog _log;
        private readonly object _lock = new();
        private readonly System.Threading.Timer _timer;
        private readonly Dictionary<Mx3FileKind, State> _states;
        private int _polling;

        public Mx3ImportMonitor(string outputDir, AppLog log)
        {
            _outputDir = outputDir;
            _log = log;
            _states = new Dictionary<Mx3FileKind, State>
            {
                [Mx3FileKind.Atm] = new State(Path.Combine(outputDir, Mx3ExportService.AtmFileName)),
                [Mx3FileKind.Smile] = new State(Path.Combine(outputDir, Mx3ExportService.SmileFileName)),
            };
            _timer = new System.Threading.Timer(_ => Poll(), null, Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>Anropas på bakgrundstråd när någon fil byter status.</summary>
        public event EventHandler? Changed;

        public void Start() => _timer.Change(TimeSpan.Zero, PollInterval);

        public Mx3FileStatus GetStatus(Mx3FileKind kind)
        {
            lock (_lock)
            {
                var s = _states[kind];
                return new Mx3FileStatus(kind, s.Pending, s.PendingSince, s.LastCompleted, s.LastDuration);
            }
        }

        public static string Label(Mx3FileKind kind) => kind == Mx3FileKind.Atm ? "ATM" : "Smile";

        /// <summary>Anropas direkt efter att vi själva skrivit filen, så UI:t visar "importing" utan fördröjning.</summary>
        public void NotifyWritten(Mx3FileKind kind)
        {
            lock (_lock)
            {
                var s = _states[kind];
                s.Pending = true;
                s.PendingSince ??= DateTime.Now;
                s.StuckReported = false;
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }

        private void Poll()
        {
            if (Interlocked.Exchange(ref _polling, 1) == 1)
                return;

            try
            {
                // File.Exists returnerar false (utan fel) när nätverket är nere – då vet vi ingenting
                if (!Directory.Exists(_outputDir))
                    return;

                var changed = false;
                foreach (var (kind, state) in _states)
                {
                    bool exists;
                    try { exists = File.Exists(state.Path); }
                    catch { continue; }

                    var now = DateTime.Now;
                    string? info = null, success = null, warning = null;

                    lock (_lock)
                    {
                        if (exists && !state.Pending)
                        {
                            state.Pending = true;
                            state.PendingSince = now;
                            state.StuckReported = false;
                            info = $"{Label(kind)} import file detected – waiting for MX3";
                            changed = true;
                        }
                        else if (!exists && state.Pending)
                        {
                            state.LastDuration = state.PendingSince.HasValue ? now - state.PendingSince.Value : null;
                            state.Pending = false;
                            state.PendingSince = null;
                            state.LastCompleted = now;
                            success = state.LastDuration is { } d
                                ? $"MX3 imported {Label(kind)} ({FormatDuration(d)})"
                                : $"MX3 imported {Label(kind)}";
                            changed = true;
                        }
                        else if (exists && state.Pending && !state.StuckReported &&
                                 state.PendingSince.HasValue && now - state.PendingSince.Value > StuckAfter)
                        {
                            state.StuckReported = true;
                            warning = $"{Label(kind)} file has waited more than {StuckAfter.TotalMinutes:0} min – is the MX3 import running?";
                            changed = true;
                        }
                    }

                    if (info != null) _log.Info(info);
                    if (success != null) _log.Success(success);
                    if (warning != null) _log.Warning(warning);
                }

                if (changed)
                    Changed?.Invoke(this, EventArgs.Empty);
            }
            catch
            {
                // Nätverksfel – nästa poll försöker igen
            }
            finally
            {
                Volatile.Write(ref _polling, 0);
            }
        }

        public static string FormatDuration(TimeSpan d)
            => d.TotalMinutes >= 1 ? $"{(int)d.TotalMinutes}m {d.Seconds:00}s" : $"{Math.Max(0, (int)d.TotalSeconds)}s";

        public void Dispose() => _timer.Dispose();

        private sealed class State
        {
            public State(string path) => Path = path;

            public string Path { get; }
            public bool Pending { get; set; }
            public DateTime? PendingSince { get; set; }
            public DateTime? LastCompleted { get; set; }
            public TimeSpan? LastDuration { get; set; }
            public bool StuckReported { get; set; }
        }
    }
}