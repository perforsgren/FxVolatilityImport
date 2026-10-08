// Services/BloombergService.cs
using System.Collections.Concurrent;
using System.Globalization;
using FxVolatilityImport.Models;
using BbgCorrelationId = Bloomberglp.Blpapi.CorrelationID;
using BbgElement = Bloomberglp.Blpapi.Element;
using BbgEvent = Bloomberglp.Blpapi.Event;
using BbgMessage = Bloomberglp.Blpapi.Message;
using BbgName = Bloomberglp.Blpapi.Name;
using BbgService = Bloomberglp.Blpapi.Service;
using BbgSession = Bloomberglp.Blpapi.Session;
using BbgSessionOptions = Bloomberglp.Blpapi.SessionOptions;

namespace FxVolatilityImport.Services
{
    public enum BloombergState { Disconnected, Connecting, Connected }

    public sealed class BloombergUnavailableException : Exception
    {
        public BloombergUnavailableException(string message) : base(message) { }
    }

    public sealed class BloombergRequestException : Exception
    {
        public BloombergRequestException(string message) : base(message) { }
    }

    /// <summary>
    /// Bloomberg Desktop API-klient med asynkron session.
    ///
    /// - Sessionens statushändelser (ConnectionDown/Terminated) fångas direkt och väntande anrop avbryts.
    /// - En vakt (watchdog) återansluter automatiskt med backoff (5s, 10s, 20s ... max 2 min) och ger aldrig upp.
    /// - Var 5:e minut görs ett heartbeat-anrop för att upptäcka "zombie-sessioner" (t.ex. efter natten,
    ///   när Terminalen loggats ut eller bbcomm startats om utan att sessionen märkt det).
    /// - Alla anrop har timeout, så en hängande Bloomberg kan aldrig låsa appen.
    /// </summary>
    public sealed class BloombergService : IDisposable
    {
        private const string RefDataServiceName = "//blp/refdata";
        private const string HeartbeatTicker = "EURSEK Curncy";

        private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan OpenServiceTimeout = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(2);

        private static readonly BbgName SessionStarted = BbgName.GetName("SessionStarted");
        private static readonly BbgName SessionStartupFailure = BbgName.GetName("SessionStartupFailure");
        private static readonly BbgName SessionConnectionDown = BbgName.GetName("SessionConnectionDown");
        private static readonly BbgName SessionTerminated = BbgName.GetName("SessionTerminated");
        private static readonly BbgName RequestFailure = BbgName.GetName("RequestFailure");

        private static readonly BbgName SecurityData = BbgName.GetName("securityData");
        private static readonly BbgName Security = BbgName.GetName("security");
        private static readonly BbgName FieldData = BbgName.GetName("fieldData");
        private static readonly BbgName SecurityError = BbgName.GetName("securityError");
        private static readonly BbgName FieldExceptions = BbgName.GetName("fieldExceptions");
        private static readonly BbgName FieldId = BbgName.GetName("fieldId");
        private static readonly BbgName ErrorInfo = BbgName.GetName("errorInfo");
        private static readonly BbgName MessageElement = BbgName.GetName("message");
        private static readonly BbgName Description = BbgName.GetName("description");
        private static readonly BbgName ResponseError = BbgName.GetName("responseError");
        private static readonly BbgName Reason = BbgName.GetName("reason");

        private readonly AppLog _log;
        private readonly string _host;
        private readonly int _port;
        private readonly object _stateLock = new();
        private readonly SemaphoreSlim _connectLock = new(1, 1);
        private readonly SemaphoreSlim _requestLock = new(1, 1);
        private readonly ConcurrentDictionary<long, PendingRequest> _pending = new();
        private readonly System.Threading.Timer _watchdog;

        private BbgSession? _session;
        private BbgService? _refData;
        private TaskCompletionSource<bool>? _startTcs;
        private BloombergState _state = BloombergState.Disconnected;
        private long _correlationSeq;
        private int _failedAttempts;
        private DateTime _nextAttempt = DateTime.MinValue;
        private DateTime _lastHeartbeat = DateTime.MinValue;
        private DateTime? _connectedSince;
        private string? _lastError;
        private volatile bool _hasLiveData;
        private int _watchdogRunning;
        private volatile bool _disposed;

        public BloombergService(AppLog log, string serverHost = "localhost", int serverPort = 8194)
        {
            _log = log;
            _host = serverHost;
            _port = serverPort;
            _watchdog = new System.Threading.Timer(_ => OnWatchdogTick(), null, Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>Anropas på bakgrundstråd när tillståndet ändras.</summary>
        public event EventHandler<BloombergState>? StateChanged;

        public BloombergState State { get { lock (_stateLock) return _state; } }
        public bool IsConnected => State == BloombergState.Connected;

        /// <summary>True när senaste heartbeat/hämtning faktiskt gav data (Terminalen är inloggad).</summary>
        public bool HasLiveData => _hasLiveData;

        public DateTime? ConnectedSince { get { lock (_stateLock) return _connectedSince; } }
        public DateTime? LastHeartbeat { get { lock (_stateLock) return _lastHeartbeat == DateTime.MinValue ? null : _lastHeartbeat; } }
        public string? LastError { get { lock (_stateLock) return _lastError; } }
        public int FailedAttempts { get { lock (_stateLock) return _failedAttempts; } }

        public DateTime? NextReconnectAttempt
        {
            get { lock (_stateLock) return _state == BloombergState.Disconnected ? _nextAttempt : null; }
        }

        /// <summary>Startar vakten som kopplar upp, återansluter och kör heartbeat.</summary>
        public void Start() => _watchdog.Change(TimeSpan.Zero, WatchdogInterval);

        // ------------------------------------------------------------------
        // Uppkoppling
        // ------------------------------------------------------------------

        private async void OnWatchdogTick()
        {
            if (_disposed || Interlocked.Exchange(ref _watchdogRunning, 1) == 1)
                return;

            try
            {
                DateTime nextAttempt, lastHeartbeat;
                BloombergState state;
                lock (_stateLock)
                {
                    state = _state;
                    nextAttempt = _nextAttempt;
                    lastHeartbeat = _lastHeartbeat;
                }

                if (state == BloombergState.Disconnected && DateTime.Now >= nextAttempt)
                    await ConnectAsync().ConfigureAwait(false);
                else if (state == BloombergState.Connected && DateTime.Now - lastHeartbeat >= HeartbeatInterval)
                    await HeartbeatAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Error($"Bloomberg watchdog: {ex.Message}");
            }
            finally
            {
                Volatile.Write(ref _watchdogRunning, 0);
            }
        }

        /// <summary>
        /// Kopplar upp mot Bloomberg. Med force=true startas en helt ny session även om den gamla ser frisk ut.
        /// </summary>
        public async Task<bool> ConnectAsync(bool force = false, CancellationToken ct = default)
        {
            await _connectLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_disposed)
                    return false;

                if (State == BloombergState.Connected && !force)
                    return true;

                if (force)
                {
                    lock (_stateLock) _failedAttempts = 0;
                }

                TearDownSession("new connection attempt");
                SetState(BloombergState.Connecting);

                var startTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var options = new BbgSessionOptions { ServerHost = _host, ServerPort = _port };
                var session = new BbgSession(options, OnSessionEvent);

                lock (_stateLock)
                {
                    _session = session;
                    _startTcs = startTcs;
                }

                // StartAsync returnerar direkt (void). Resultatet kommer som SessionStarted/SessionStartupFailure
                // i OnSessionEvent. Kastar den här fångas det i catch nedan.
                session.StartAsync();

                if (!await WaitWithTimeout(startTcs.Task, StartTimeout, ct).ConfigureAwait(false))
                    return ConnectFailed("session start failed or timed out (is the Bloomberg Terminal running?)");

                var openTask = Task.Run(() => session.OpenService(RefDataServiceName), ct);
                if (!await WaitWithTimeout(openTask, OpenServiceTimeout, ct).ConfigureAwait(false))
                    return ConnectFailed($"could not open {RefDataServiceName}");

                var service = session.GetService(RefDataServiceName);

                lock (_stateLock)
                {
                    if (!ReferenceEquals(_session, session))
                        return false; // sessionen dog medan vi öppnade tjänsten

                    _refData = service;
                    _failedAttempts = 0;
                    _lastError = null;
                    _connectedSince = DateTime.Now;
                    _lastHeartbeat = DateTime.MinValue; // kör heartbeat direkt för att verifiera att data kommer
                }

                SetState(BloombergState.Connected);
                _log.Success("Bloomberg connected");
                return true;
            }
            catch (OperationCanceledException)
            {
                ConnectFailed("cancelled");
                throw;
            }
            catch (Exception ex)
            {
                return ConnectFailed(ex.Message);
            }
            finally
            {
                _connectLock.Release();
            }
        }

        private static async Task<bool> WaitWithTimeout(Task<bool> task, TimeSpan timeout, CancellationToken ct)
        {
            try
            {
                return await task.WaitAsync(timeout, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return false;
            }
        }

        private bool ConnectFailed(string reason)
        {
            TearDownSession(reason);

            int attempts;
            TimeSpan delay;
            lock (_stateLock)
            {
                attempts = ++_failedAttempts;
                var seconds = Math.Min(MaxBackoff.TotalSeconds, 5 * Math.Pow(2, Math.Min(attempts - 1, 6)));
                delay = TimeSpan.FromSeconds(seconds);
                _nextAttempt = DateTime.Now + delay;
                _lastError = reason;
            }

            _hasLiveData = false;
            SetState(BloombergState.Disconnected);

            // Logga de första försöken och sedan glesare, så loggen inte fylls under en lång nedtid (t.ex. natten)
            if (attempts <= 3 || attempts % 20 == 0)
                _log.Warning($"Bloomberg connect failed (attempt {attempts}): {reason}. Retrying in {delay.TotalSeconds:0}s");

            return false;
        }

        /// <summary>
        /// Markerar sessionen som död. Själva nedstängningen sker i nästa ConnectAsync
        /// (Stop får inte anropas från Bloombergs händelsetråd).
        /// </summary>
        private void MarkDisconnected(string reason)
        {
            BloombergState previous;
            TaskCompletionSource<bool>? startTcs;
            lock (_stateLock)
            {
                previous = _state;
                if (previous == BloombergState.Disconnected)
                    return;

                startTcs = _startTcs;
                _nextAttempt = DateTime.Now + TimeSpan.FromSeconds(2);
                _lastError = reason;
            }

            startTcs?.TrySetResult(false);
            FailAllPending(new BloombergUnavailableException($"Bloomberg connection lost ({reason})"));
            _hasLiveData = false;

            if (previous == BloombergState.Connected)
            {
                SetState(BloombergState.Disconnected);
                _log.Warning($"Bloomberg connection lost: {reason}. Reconnecting automatically…");
            }
        }

        private void TearDownSession(string reason, bool waitForStop = false)
        {
            BbgSession? old;
            TaskCompletionSource<bool>? startTcs;
            lock (_stateLock)
            {
                old = _session;
                startTcs = _startTcs;
                _session = null;
                _refData = null;
                _startTcs = null;
                _connectedSince = null;
            }

            startTcs?.TrySetResult(false);
            FailAllPending(new BloombergUnavailableException($"Bloomberg session closed ({reason})"));

            if (old == null)
                return;

            var stopTask = Task.Run(() =>
            {
                try { old.Stop(); }
                catch { /* sessionen kan redan vara död */ }
            });

            if (waitForStop)
                stopTask.Wait(TimeSpan.FromSeconds(3));
        }

        private void SetState(BloombergState newState)
        {
            bool changed;
            lock (_stateLock)
            {
                changed = _state != newState;
                _state = newState;
            }

            if (changed)
                StateChanged?.Invoke(this, newState);
        }

        // ------------------------------------------------------------------
        // Händelser från Bloomberg (körs på Bloombergs händelsetråd)
        // ------------------------------------------------------------------

        private void OnSessionEvent(BbgEvent ev, BbgSession session)
        {
            try
            {
                lock (_stateLock)
                {
                    if (!ReferenceEquals(session, _session))
                        return; // händelse från en gammal session som håller på att stängas
                }

                switch (ev.Type)
                {
                    case BbgEvent.EventType.SESSION_STATUS:
                        foreach (BbgMessage msg in ev)
                            HandleSessionStatus(msg);
                        break;

                    case BbgEvent.EventType.PARTIAL_RESPONSE:
                    case BbgEvent.EventType.RESPONSE:
                    case BbgEvent.EventType.REQUEST_STATUS:
                        foreach (BbgMessage msg in ev)
                            HandleResponse(ev.Type, msg);
                        break;
                }
            }
            catch (Exception ex)
            {
                _log.Error($"Bloomberg event handling failed: {ex.Message}");
            }
        }

        private void HandleSessionStatus(BbgMessage msg)
        {
            TaskCompletionSource<bool>? startTcs;
            lock (_stateLock) startTcs = _startTcs;

            var type = msg.MessageType;
            if (type == SessionStarted)
                startTcs?.TrySetResult(true);
            else if (type == SessionStartupFailure)
                startTcs?.TrySetResult(false);
            else if (type == SessionConnectionDown || type == SessionTerminated)
                MarkDisconnected(type.ToString());
        }

        private void HandleResponse(BbgEvent.EventType type, BbgMessage msg)
        {
            var correlationId = msg.CorrelationID;
            if (correlationId == null || !_pending.TryGetValue(correlationId.Value, out var pending))
                return;

            if (type == BbgEvent.EventType.REQUEST_STATUS)
            {
                if (msg.MessageType == RequestFailure)
                    pending.Fail(new BloombergRequestException($"Request failed: {DescribeChild(msg, Reason)}"));
                return;
            }

            if (msg.HasElement(ResponseError))
            {
                pending.Fail(new BloombergRequestException($"Response error: {DescribeChild(msg, ResponseError)}"));
                return;
            }

            if (msg.HasElement(SecurityData))
                ParseSecurityData(msg.GetElement(SecurityData), pending);

            if (type == BbgEvent.EventType.RESPONSE)
                pending.Complete();
        }

        private static void ParseSecurityData(BbgElement securities, PendingRequest pending)
        {
            for (int i = 0; i < securities.NumValues; i++)
            {
                var security = securities.GetValueAsElement(i);
                var ticker = security.GetElementAsString(Security);

                if (security.HasElement(SecurityError))
                {
                    pending.AddError($"{ticker}: {ErrorText(security.GetElement(SecurityError))}");
                    continue;
                }

                if (security.HasElement(FieldData))
                {
                    var fields = security.GetElement(FieldData);
                    for (int j = 0; j < fields.NumElements; j++)
                    {
                        var field = fields.GetElement(j);
                        if (TryReadDouble(field, out var value))
                            pending.SetValue(ticker, field.Name.ToString(), value);
                    }
                }

                if (security.HasElement(FieldExceptions))
                {
                    var exceptions = security.GetElement(FieldExceptions);
                    for (int k = 0; k < exceptions.NumValues; k++)
                    {
                        var fieldException = exceptions.GetValueAsElement(k);
                        var fieldId = fieldException.HasElement(FieldId) ? fieldException.GetElementAsString(FieldId) : "?";
                        var info = fieldException.HasElement(ErrorInfo) ? ErrorText(fieldException.GetElement(ErrorInfo)) : "unknown error";
                        pending.AddError($"{ticker} {fieldId}: {info}");
                    }
                }
            }
        }

        private static string DescribeChild(BbgMessage msg, BbgName child)
        {
            try { return ErrorText(msg.GetElement(child)); }
            catch { return msg.ToString() ?? "unknown error"; }
        }

        private static string ErrorText(BbgElement error)
        {
            if (error.HasElement(MessageElement))
                return error.GetElementAsString(MessageElement);
            if (error.HasElement(Description))
                return error.GetElementAsString(Description);
            return error.ToString() ?? "unknown error";
        }

        private static bool TryReadDouble(BbgElement field, out double value)
        {
            try
            {
                value = field.GetValueAsFloat64();
                return double.IsFinite(value);
            }
            catch
            {
                // Fallback: tolka strängen med InvariantCulture (aldrig användarens regionala inställningar)
                try
                {
                    return double.TryParse(field.GetValueAsString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                           && double.IsFinite(value);
                }
                catch
                {
                    value = double.NaN;
                    return false;
                }
            }
        }

        // ------------------------------------------------------------------
        // Anrop
        // ------------------------------------------------------------------

        private async Task<RefDataResult> RequestAsync(
            IReadOnlyCollection<string> tickers,
            IReadOnlyCollection<string> fields,
            TimeSpan timeout,
            CancellationToken ct)
        {
            BbgSession? session;
            BbgService? service;
            lock (_stateLock)
            {
                session = _state == BloombergState.Connected ? _session : null;
                service = _refData;
            }

            if (session == null || service == null)
                throw new BloombergUnavailableException("Bloomberg is not connected");

            var request = service.CreateRequest("ReferenceDataRequest");
            foreach (var ticker in tickers)
                request.Append("securities", ticker);
            foreach (var field in fields)
                request.Append("fields", field);

            var id = Interlocked.Increment(ref _correlationSeq);
            var pending = new PendingRequest();
            _pending[id] = pending;

            try
            {
                session.SendRequest(request, new BbgCorrelationId(id));
                try
                {
                    return await pending.Completion.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    throw new TimeoutException($"Bloomberg did not answer within {timeout.TotalSeconds:0}s");
                }
            }
            finally
            {
                _pending.TryRemove(id, out _);
            }
        }

        private void FailAllPending(Exception ex)
        {
            foreach (var pending in _pending.Values)
                pending.Fail(ex);
        }

        /// <summary>
        /// Hämtar ATM (bid/ask) och smile (RR/BF mid) för alla par med IsLive=true.
        /// Kopplar upp först om det behövs. Saknade värden returneras som NaN.
        /// </summary>
        public async Task<VolatilityLoadResult> LoadVolatilityDataAsync(
            IReadOnlyList<CurrencyPairConfig> pairs,
            CancellationToken ct = default)
        {
            var livePairs = pairs.Where(p => p.IsLive).ToList();
            if (livePairs.Count == 0)
                return new VolatilityLoadResult(Array.Empty<VolatilityTenor>(), Array.Empty<string>(), DateTime.Now);

            await _requestLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!IsConnected && !await ConnectAsync(false, ct).ConfigureAwait(false))
                    throw new BloombergUnavailableException($"Bloomberg is not connected ({LastError ?? "unknown reason"})");

                var atmTickers = new List<string>();
                var smileTickers = new List<string>();
                foreach (var pair in livePairs)
                {
                    var bbgPair = CurrencyPairMapper.ToBloombergPair(pair.CurrencyPair);
                    foreach (var tenor in VolTenors.All)
                    {
                        atmTickers.Add(AtmTicker(bbgPair, tenor, pair.AtmSource));
                        smileTickers.Add(SmileTicker(bbgPair, "25R", tenor, pair.SmileSource));
                        smileTickers.Add(SmileTicker(bbgPair, "10R", tenor, pair.SmileSource));
                        smileTickers.Add(SmileTicker(bbgPair, "25B", tenor, pair.SmileSource));
                        smileTickers.Add(SmileTicker(bbgPair, "10B", tenor, pair.SmileSource));
                    }
                }

                var atm = await RequestAsync(atmTickers, new[] { "PX_BID", "PX_ASK" }, RequestTimeout, ct).ConfigureAwait(false);
                var smile = await RequestAsync(smileTickers, new[] { "PX_MID" }, RequestTimeout, ct).ConfigureAwait(false);

                var data = new List<VolatilityTenor>();
                foreach (var pair in livePairs)
                {
                    var bbgPair = CurrencyPairMapper.ToBloombergPair(pair.CurrencyPair);
                    foreach (var tenor in VolTenors.All)
                    {
                        var atmTicker = AtmTicker(bbgPair, tenor, pair.AtmSource);
                        var src = pair.SmileSource;
                        data.Add(new VolatilityTenor
                        {
                            CurrencyPair = pair.CurrencyPair,
                            Tenor = tenor,
                            AtmBid = atm.Get(atmTicker, "PX_BID"),
                            AtmAsk = atm.Get(atmTicker, "PX_ASK"),
                            RR25D = CurrencyPairMapper.AdjustRiskReversal(smile.Get(SmileTicker(bbgPair, "25R", tenor, src), "PX_MID"), pair.CurrencyPair),
                            RR10D = CurrencyPairMapper.AdjustRiskReversal(smile.Get(SmileTicker(bbgPair, "10R", tenor, src), "PX_MID"), pair.CurrencyPair),
                            BF25D = smile.Get(SmileTicker(bbgPair, "25B", tenor, src), "PX_MID"),
                            BF10D = smile.Get(SmileTicker(bbgPair, "10B", tenor, src), "PX_MID"),
                        });
                    }
                }

                var gotData = data.Any(d => double.IsFinite(d.AtmBid));
                _hasLiveData = gotData;
                if (gotData)
                {
                    lock (_stateLock) _lastHeartbeat = DateTime.Now;
                }

                var errors = atm.Errors.Concat(smile.Errors).ToList();
                return new VolatilityLoadResult(data, errors, DateTime.Now);
            }
            catch (TimeoutException ex)
            {
                // En timeout betyder nästan alltid en hängd session – starta om den
                MarkDisconnected("request timed out");
                throw new BloombergUnavailableException(ex.Message);
            }
            finally
            {
                _requestLock.Release();
            }
        }

        private static string AtmTicker(string bbgPair, string tenor, string source)
            => $"{bbgPair}V{tenor} {source} Curncy";

        private static string SmileTicker(string bbgPair, string kind, string tenor, string source)
            => $"{bbgPair}{kind}{tenor} {source} Curncy";

        private async Task HeartbeatAsync()
        {
            // Pågår en hämtning fungerar den som hälsokontroll
            if (!await _requestLock.WaitAsync(0).ConfigureAwait(false))
                return;

            try
            {
                var result = await RequestAsync(
                    new[] { HeartbeatTicker }, new[] { "PX_LAST" }, HeartbeatTimeout, CancellationToken.None).ConfigureAwait(false);

                lock (_stateLock) _lastHeartbeat = DateTime.Now;

                var hasData = double.IsFinite(result.Get(HeartbeatTicker, "PX_LAST"));
                var hadData = _hasLiveData;
                _hasLiveData = hasData;

                if (!hasData)
                {
                    var why = result.Errors.Count > 0 ? string.Join("; ", result.Errors) : "no value returned";
                    _log.Warning($"Bloomberg is connected but returns no data ({why}). Is the Terminal logged in?");
                }
                else if (!hadData)
                {
                    StateChanged?.Invoke(this, BloombergState.Connected); // uppdatera UI: data flödar igen
                }
            }
            catch (Exception ex)
            {
                MarkDisconnected($"heartbeat failed: {ex.Message}");
            }
            finally
            {
                _requestLock.Release();
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _watchdog.Dispose();
            TearDownSession("application closing", waitForStop: true);
        }

        // ------------------------------------------------------------------

        private sealed class PendingRequest
        {
            private readonly object _lock = new();
            private readonly Dictionary<string, Dictionary<string, double>> _values = new(StringComparer.OrdinalIgnoreCase);
            private readonly List<string> _errors = new();

            public TaskCompletionSource<RefDataResult> Completion { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public void SetValue(string ticker, string field, double value)
            {
                lock (_lock)
                {
                    if (!_values.TryGetValue(ticker, out var fields))
                        _values[ticker] = fields = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                    fields[field] = value;
                }
            }

            public void AddError(string error)
            {
                lock (_lock) _errors.Add(error);
            }

            public void Complete()
            {
                lock (_lock)
                    Completion.TrySetResult(new RefDataResult(_values, _errors.ToList()));
            }

            public void Fail(Exception ex) => Completion.TrySetException(ex);
        }

        private sealed record RefDataResult(
            IReadOnlyDictionary<string, Dictionary<string, double>> Values,
            IReadOnlyList<string> Errors)
        {
            public double Get(string ticker, string field)
                => Values.TryGetValue(ticker, out var fields) && fields.TryGetValue(field, out var v) ? v : double.NaN;
        }
    }
}