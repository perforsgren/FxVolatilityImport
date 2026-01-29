// Models/VolatilityTenor.cs
namespace FxVolatilityImport.Models
{
    public class VolatilityTenor
    {
        public string CurrencyPair { get; set; } = "";
        public string Tenor { get; set; } = "";
        public double AtmBid { get; set; }
        public double AtmAsk { get; set; }
        public double RR25D { get; set; }
        public double RR10D { get; set; }
        public double BF25D { get; set; }
        public double BF10D { get; set; }
    }
}
