// Models/AppSettings.cs
namespace FxVolatilityImport.Models
{
    public class AppSettings
    {
        public List<CurrencyPairConfig> CurrencyPairs { get; set; } = new();
        public DateTime LastSaved { get; set; }
    }
}
