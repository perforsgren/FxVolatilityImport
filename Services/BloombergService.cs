// Services/BloombergService.cs
using Bloomberglp.Blpapi;
using FxVolatilityImport.Models;
using System.Xml.Linq;
using Message = Bloomberglp.Blpapi.Message;

namespace FxVolatilityImport.Services
{
    public class BloombergService : IDisposable
    {
        private Session? _session;
        private readonly string _serverHost;
        private readonly int _serverPort;
        private readonly Dictionary<string, Dictionary<string, string>> _values = new();

        private static readonly Name SECURITY_DATA = new Name("securityData");
        private static readonly Name SECURITY = new Name("security");
        private static readonly Name FIELD_DATA = new Name("fieldData");
        private static readonly Name SECURITY_ERROR = new Name("securityError");
        private static readonly Name FIELD_EXCEPTIONS = new Name("fieldExceptions");

        private readonly string[] _tenors =
            { "ON", "1W", "2W", "1M", "2M", "3M", "6M", "1Y", "2Y", "3Y" };

        public BloombergService(string serverHost = "localhost", int serverPort = 8194)
        {
            _serverHost = serverHost;
            _serverPort = serverPort;
        }

        public bool Connect()
        {
            try
            {
                var sessionOptions = new SessionOptions
                {
                    ServerHost = _serverHost,
                    ServerPort = _serverPort
                };

                _session = new Session(sessionOptions);
                return _session.Start() && _session.OpenService("//blp/refdata");
            }
            catch
            {
                return false;
            }
        }

        public List<VolatilityTenor> LoadVolatilityData(List<CurrencyPairConfig> pairs)
        {
            var result = new List<VolatilityTenor>();
            var livePairs = pairs.Where(p => p.IsLive).ToList();

            if (!livePairs.Any() || _session == null)
                return result;

            // Initiera alla tenor-objekt (använd risk system pair för visning)
            foreach (var pair in livePairs)
            {
                foreach (var tenor in _tenors)
                {
                    result.Add(new VolatilityTenor
                    {
                        CurrencyPair = pair.CurrencyPair,
                        Tenor = tenor
                    });
                }
            }

            // === Hämta ATM data ===
            var atmTickers = new List<string>();

            foreach (var pair in livePairs)
            {
                var bbgPair = CurrencyPairMapper.ToBloombergPair(pair.CurrencyPair);

                foreach (var tenor in _tenors)
                {
                    var ticker = $"{bbgPair}V{tenor} {pair.AtmSource} Curncy";
                    atmTickers.Add(ticker);
                }
            }

            FetchData(atmTickers, new[] { "PX_BID", "PX_ASK" });

            // Fyll i ATM-värden
            int idx = 0;
            foreach (var pair in livePairs)
            {
                var bbgPair = CurrencyPairMapper.ToBloombergPair(pair.CurrencyPair);

                foreach (var tenor in _tenors)
                {
                    var ticker = $"{bbgPair}V{tenor} {pair.AtmSource} Curncy";
                    result[idx].AtmBid = GetValue(ticker, "PX_BID");
                    result[idx].AtmAsk = GetValue(ticker, "PX_ASK");
                    idx++;
                }
            }

            // === Hämta Smile data (RR/BF) ===
            var smileTickers = new List<string>();

            foreach (var pair in livePairs)
            {
                var bbgPair = CurrencyPairMapper.ToBloombergPair(pair.CurrencyPair);

                foreach (var tenor in _tenors)
                {
                    smileTickers.Add($"{bbgPair}25R{tenor} {pair.SmileSource} Curncy");
                    smileTickers.Add($"{bbgPair}10R{tenor} {pair.SmileSource} Curncy");
                    smileTickers.Add($"{bbgPair}25B{tenor} {pair.SmileSource} Curncy");
                    smileTickers.Add($"{bbgPair}10B{tenor} {pair.SmileSource} Curncy");
                }
            }

            FetchData(smileTickers, new[] { "PX_MID" });

            // Fyll i Smile-värden (med RR-justering för inverterade par)
            idx = 0;
            int tickerIdx = 0;
            foreach (var pair in livePairs)
            {
                var bbgPair = CurrencyPairMapper.ToBloombergPair(pair.CurrencyPair);

                foreach (var tenor in _tenors)
                {
                    var rr25 = GetValue(smileTickers[tickerIdx], "PX_MID");
                    var rr10 = GetValue(smileTickers[tickerIdx + 1], "PX_MID");
                    var bf25 = GetValue(smileTickers[tickerIdx + 2], "PX_MID");
                    var bf10 = GetValue(smileTickers[tickerIdx + 3], "PX_MID");

                    result[idx].RR25D = CurrencyPairMapper.AdjustRiskReversal(rr25, pair.CurrencyPair);
                    result[idx].RR10D = CurrencyPairMapper.AdjustRiskReversal(rr10, pair.CurrencyPair);
                    result[idx].BF25D = bf25;
                    result[idx].BF10D = bf10;

                    idx++;
                    tickerIdx += 4;
                }
            }

            return result;
        }

        private void FetchData(List<string> tickers, string[] fields)
        {
            if (_session == null) return;

            var refDataService = _session.GetService("//blp/refdata");
            var request = refDataService.CreateRequest("ReferenceDataRequest");

            foreach (var ticker in tickers)
                request.Append("securities", ticker);

            foreach (var field in fields)
                request.Append("fields", field);

            _session.SendRequest(request, null);
            ProcessResponses();
        }

        private void ProcessResponses()
        {
            bool done = false;
            while (!done && _session != null)
            {
                Event eventObj = _session.NextEvent();

                if (eventObj.Type == Event.EventType.RESPONSE ||
                    eventObj.Type == Event.EventType.PARTIAL_RESPONSE)
                {
                    foreach (Message msg in eventObj)
                    {
                        if (msg.HasElement(SECURITY_DATA))
                        {
                            var securities = msg.GetElement(SECURITY_DATA);
                            for (int i = 0; i < securities.NumValues; i++)
                            {
                                var security = securities.GetValueAsElement(i);
                                var ticker = security.GetElementAsString(SECURITY);

                                if (!_values.ContainsKey(ticker))
                                    _values[ticker] = new Dictionary<string, string>();

                                if (security.HasElement(FIELD_DATA))
                                {
                                    var fieldData = security.GetElement(FIELD_DATA);
                                    for (int j = 0; j < fieldData.NumElements; j++)
                                    {
                                        var field = fieldData.GetElement(j);
                                        _values[ticker][field.Name.ToString()] = field.GetValueAsString();
                                    }
                                }
                            }
                        }
                    }

                    if (eventObj.Type == Event.EventType.RESPONSE)
                        done = true;
                }
            }
        }

        private double GetValue(string ticker, string field)
        {
            if (_values.TryGetValue(ticker, out var fields) &&
                fields.TryGetValue(field, out var value) &&
                double.TryParse(value, out var result))
            {
                return result;
            }
            return 0;
        }

        public void Dispose()
        {
            _session?.Stop();
            _session = null;
        }
    }
}
