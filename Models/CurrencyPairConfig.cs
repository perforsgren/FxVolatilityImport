// Models/CurrencyPairConfig.cs
namespace FxVolatilityImport.Models
{
    public class CurrencyPairConfig
    {
        public string CurrencyPair { get; set; } = "";  // t.ex. "EURSEK"
        public string AtmSource { get; set; } = "BGN";
        public string SmileSource { get; set; } = "BGN";
        public bool IsLive { get; set; } = true;
    }
}
