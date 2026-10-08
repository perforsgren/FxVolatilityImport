// Services/SchedulerLeaseService.cs
using System.IO;
using System.Text;
using System.Text.Json;
using FxVolatilityImport.Models;

namespace FxVolatilityImport.Services
{
    /// <summary>
    /// Väljer EN master bland alla körande instanser, så att den schemalagda importen bara körs en gång.
    ///
    /// Mekanism (två filer på nätverksdisken):
    /// - scheduler.lock: master håller filen öppen exklusivt (FileShare.None). Filservern hindrar då
    ///   alla andra från att öppna den, så bara en instans i taget kan vara master.
    /// - scheduler.json: master skriver vem den är, heartbeat och hur senaste schemalagda import gick.
    ///   Alla instanser läser filen för att visa status, och en ny master läser den för att inte köra
    ///   om en slot som redan importerats.
    ///
    /// Failover:
    /// - Stängs eller kraschar master-appen släpper filservern låset, och nästa instans tar över inom ca 10 s.
    /// - Får master ingen Bloomberg-data på 90 s (t.ex. Terminalen utloggad för att man loggat in på
    ///   telefonen) släpper den rollen frivilligt, så att någon annan med inloggad Terminal tar över.
    /// - Bara instanser med fungerande Bloomberg-data försöker bli master. Det finns ingen prioritetsordning:
    ///   den första som ser att låset är ledigt (kollar var 10:e s) tar det.
    /// </summary>
    public sealed class SchedulerLeaseService : IDisposable
    {
        private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(10);
        public static readonly TimeSpan IneligibleGrace = TimeSpan.FromSeconds(90);
        public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(2);

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private readonly string _lockPath;
        private readonly string _infoPath;
        private readonly AppLog _log;
        private readonly Func<bool> _isEligible;
        private readonly object _ioLock = new();
        private readonly System.Threading.Timer _timer;

        private FileStream? _lockStream;
        private SchedulerLeaseInfo _info = new();
        private DateTime? _ineligibleSince;
        private long _handoverAtTicks;
        private volatile bool _isMaster;
        private volatile SchedulerLeaseInfo? _holder;
        private volatile bool _disposed;

        public SchedulerLeaseService(string lockFilePath, AppLog log, Func<bool> isEligible)
        {
            _lockPath = lockFilePath;
            _infoPath = Path.ChangeExtension(lockFilePath, ".json");
            _log = log;
            _isEligible = isEligible;
            _timer = new System.Threading.Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>Anropas på bakgrundstråd när denna instans blir eller slutar vara master.</summary>
        public event EventHandler? MasterChanged;

        public bool IsMaster => _isMaster;

        /// <summary>Senast kända master-info (vår egen när vi är master).</summary>
        public SchedulerLeaseInfo? Holder => _holder;

        /// <summary>True om nuvarande master har skickat heartbeat de senaste 2 minuterna.</summary>
        public bool IsHolderAlive => _holder is { } holder && DateTime.Now - holder.Heartbeat <= StaleAfter;

        public DateTime? LastSuccessfulSlot => _holder?.LastSuccessfulSlot;

        /// <summary>
        /// Slot som (föregående) master försökte köra men misslyckades med. En ny master får ta över
        /// omförsöken för den, t.ex. när förra mastern lämnade rollen för att Terminalen loggats ut.
        /// </summary>
        public DateTime? LastFailedSlot => _holder is { LastRunOk: false } holder ? holder.LastRunSlot : null;

        /// <summary>
        /// När denna master lämnar över rollen om Bloomberg-data inte kommer tillbaka (null = ingen överlämning på gång).
        /// Läses låsfritt – UI:t anropar varje sekund och får inte vänta på nätverksdisken.
        /// </summary>
        public DateTime? HandoverAt
        {
            get
            {
                var ticks = Interlocked.Read(ref _handoverAtTicks);
                return ticks == 0 ? null : new DateTime(ticks);
            }
        }

        public void Start() => _timer.Change(TimeSpan.FromSeconds(3), TickInterval);

        private void Tick()
        {
            if (_disposed)
                return;

            var changed = false;
            lock (_ioLock)
            {
                if (_disposed)
                    return;

                var eligible = SafeIsEligible();

                if (_lockStream != null)
                {
                    if (eligible)
                    {
                        _ineligibleSince = null;
                    }
                    else
                    {
                        _ineligibleSince ??= DateTime.Now;
                        if (DateTime.Now - _ineligibleSince.Value >= IneligibleGrace)
                        {
                            _log.Warning($"No Bloomberg data on this PC for {IneligibleGrace.TotalSeconds:0} s – " +
                                         "handing the scheduler over to another user with a logged-in Terminal");
                            ReleaseLocked(markReleased: true);
                            changed = true;
                        }
                    }

                    if (_lockStream != null)
                    {
                        try
                        {
                            ProbeLockLocked();
                            _info.Heartbeat = DateTime.Now;
                            WriteInfoLocked();
                            _holder = Clone(_info);
                        }
                        catch (Exception ex)
                        {
                            _log.Warning($"Lost scheduler master role – network share not writable ({ex.Message})");
                            ReleaseLocked(markReleased: false);
                            changed = true;
                        }
                    }
                }
                else
                {
                    _ineligibleSince = null;
                    if (eligible && TryAcquireLocked())
                        changed = true;
                    else
                        ReadHolderLocked();
                }

                Interlocked.Exchange(ref _handoverAtTicks,
                    _lockStream != null && _ineligibleSince is DateTime since ? (since + IneligibleGrace).Ticks : 0);
            }

            if (changed)
                MasterChanged?.Invoke(this, EventArgs.Empty);
        }

        private bool SafeIsEligible()
        {
            try { return _isEligible(); }
            catch { return false; }
        }

        private bool TryAcquireLocked()
        {
            FileStream stream;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_lockPath)!);
                stream = new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                return false; // någon annan är master (eller nätverket är nere)
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }

            // Föregående masters info – så att redan körda slots inte körs igen
            var previous = TryReadInfo();

            _lockStream = stream;
            _info = new SchedulerLeaseInfo
            {
                User = Environment.UserName,
                Machine = Environment.MachineName,
                ProcessId = Environment.ProcessId,
                AcquiredAt = DateTime.Now,
                Heartbeat = DateTime.Now,
                LastSuccessfulSlot = previous?.LastSuccessfulSlot,
                LastRunSlot = previous?.LastRunSlot,
                LastRunAt = previous?.LastRunAt,
                LastRunOk = previous?.LastRunOk ?? false,
                LastRunSummary = previous?.LastRunSummary ?? ""
            };

            try
            {
                WriteInfoLocked();
            }
            catch (Exception)
            {
                ReleaseLocked(markReleased: false);
                return false;
            }

            _isMaster = true;
            _holder = Clone(_info);

            var takeover = previous != null && !string.IsNullOrEmpty(previous.User) &&
                           !(previous.User == _info.User && previous.Machine == _info.Machine)
                ? $" (previous master: {previous.User} @ {previous.Machine})"
                : "";
            _log.Success($"This instance is now scheduler master – it runs the scheduled imports{takeover}");
            return true;
        }

        /// <summary>Skriver en byte i låsfilen – kastar om filhandtaget dött (t.ex. efter nätverksavbrott).</summary>
        private void ProbeLockLocked()
        {
            var stream = _lockStream!;
            stream.Position = 0;
            stream.WriteByte((byte)'L');
            stream.Flush(true);
        }

        private void ReadHolderLocked()
        {
            var info = TryReadInfo();
            if (info != null)
                _holder = info;
            else if (!File.Exists(_infoPath))
                _holder = null;
            // annars: läsfel – behåll senaste kända info
        }

        private SchedulerLeaseInfo? TryReadInfo()
        {
            try
            {
                if (!File.Exists(_infoPath))
                    return null;

                using var fs = new FileStream(_infoPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs, Encoding.UTF8);
                var json = reader.ReadToEnd();
                return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<SchedulerLeaseInfo>(json);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Atomär skrivning (temporärfil + rename) så att ingen läser en halvskriven fil.</summary>
        private void WriteInfoLocked()
        {
            var dir = Path.GetDirectoryName(_infoPath)!;
            var tmp = Path.Combine(dir, $".{Path.GetFileName(_infoPath)}.{Environment.MachineName}.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(tmp, JsonSerializer.Serialize(_info, JsonOptions), Encoding.UTF8);

            try
            {
                for (int attempt = 1; ; attempt++)
                {
                    try
                    {
                        File.Move(tmp, _infoPath, overwrite: true);
                        return;
                    }
                    catch (IOException) when (attempt < 5)
                    {
                        Thread.Sleep(100); // någon läser filen just nu
                    }
                }
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); }
                catch { /* best effort */ }
            }
        }

        private void ReleaseLocked(bool markReleased)
        {
            var wasMaster = _isMaster;
            _ineligibleSince = null;
            Interlocked.Exchange(ref _handoverAtTicks, 0);

            if (markReleased && _lockStream != null)
            {
                try
                {
                    _info.Heartbeat = DateTime.MinValue; // visar för andra att ingen master är aktiv
                    WriteInfoLocked();
                }
                catch
                {
                    // ignorera – låset släpps ändå nedan
                }
            }

            try { _lockStream?.Dispose(); }
            catch { /* nätverket kan vara borta */ }

            _lockStream = null;
            _isMaster = false;
            if (wasMaster)
                _holder = Clone(_info);
        }

        /// <summary>Master anropar efter varje schemalagd körning. Sparas i scheduler.json så alla ser resultatet.</summary>
        public void RecordRun(DateTime slot, bool ok, string summary)
        {
            var lost = false;
            lock (_ioLock)
            {
                if (_lockStream == null)
                    return;

                if (ok)
                    _info.LastSuccessfulSlot = slot;
                _info.LastRunSlot = slot;
                _info.LastRunAt = DateTime.Now;
                _info.LastRunOk = ok;
                _info.LastRunSummary = summary;

                try
                {
                    WriteInfoLocked();
                    _holder = Clone(_info);
                }
                catch (Exception ex)
                {
                    _log.Warning($"Could not record scheduled run ({ex.Message}) – giving up master role");
                    ReleaseLocked(markReleased: false);
                    lost = true;
                }
            }

            if (lost)
                MasterChanged?.Invoke(this, EventArgs.Empty);
        }

        private static SchedulerLeaseInfo Clone(SchedulerLeaseInfo info)
            => JsonSerializer.Deserialize<SchedulerLeaseInfo>(JsonSerializer.Serialize(info))!;

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _timer.Dispose();

            lock (_ioLock)
            {
                if (_lockStream != null)
                    ReleaseLocked(markReleased: true);
            }
        }
    }
}