// Models/SharedSettings.cs
namespace FxVolatilityImport.Models
{
    /// <summary>
    /// Inställningar som delas av alla användare (JSON-fil på nätverksdisken).
    /// </summary>
    public class SharedSettings
    {
        public List<PairSourceConfig> PairSources { get; set; } = new();

        /// <summary>Global av/på för de schemalagda importerna (körs av master-instansen).</summary>
        public bool ScheduledImportEnabled { get; set; } = true;

        public DateTime LastModified { get; set; }
        public string LastModifiedBy { get; set; } = "";
    }

    public class PairSourceConfig
    {
        public string CurrencyPair { get; set; } = "";
        public string AtmSource { get; set; } = "BGN";
        public string SmileSource { get; set; } = "BGN";

        /// <summary>
        /// Om paret ska ingå i hämtning och import. Delas av alla användare,
        /// så att master-instansens schemalagda import följer samma urval.
        /// </summary>
        public bool IsLive { get; set; } = true;

        public DateTime LastUsed { get; set; }
        public string ModifiedBy { get; set; } = "";
    }
}