namespace FxVolatilityImport.Models
{
    public class SharedSettings
    {
        public List<PairSourceConfig> PairSources { get; set; } = new();
        public DateTime LastModified { get; set; }
        public string LastModifiedBy { get; set; } = "";
    }

    public class PairSourceConfig
    {
        public string CurrencyPair { get; set; } = "";
        public string AtmSource { get; set; } = "BGN";
        public string SmileSource { get; set; } = "BGN";
        public DateTime LastUsed { get; set; }
    }
}