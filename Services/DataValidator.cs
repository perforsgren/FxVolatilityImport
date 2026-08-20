using FxVolatilityImport.Models;

namespace FxVolatilityImport.Services
{
    public class DataValidator
    {
        private const double MinAtmVol = 0.5;    // 0.5% - under detta är suspekt
        private const double MaxAtmVol = 100.0;  // 100% - över detta är suspekt
        private const double MaxRR = 20.0;       // |RR| > 20 är suspekt
        private const double MaxBF = 10.0;       // BF > 10 är suspekt

        public ValidationResult Validate(List<VolatilityTenor> data)
        {
            var result = new ValidationResult();

            foreach (var tenor in data)
            {
                // Kolla ATM
                if (tenor.AtmBid <= 0 || tenor.AtmAsk <= 0)
                {
                    result.Errors.Add($"{tenor.CurrencyPair} {tenor.Tenor}: ATM bid/ask is zero or negative");
                }
                else if (tenor.AtmBid < MinAtmVol || tenor.AtmAsk < MinAtmVol)
                {
                    result.Warnings.Add($"{tenor.CurrencyPair} {tenor.Tenor}: ATM unusually low ({tenor.AtmBid:F3}/{tenor.AtmAsk:F3})");
                }
                else if (tenor.AtmBid > MaxAtmVol || tenor.AtmAsk > MaxAtmVol)
                {
                    result.Warnings.Add($"{tenor.CurrencyPair} {tenor.Tenor}: ATM unusually high ({tenor.AtmBid:F3}/{tenor.AtmAsk:F3})");
                }

                // Kolla att Ask > Bid
                if (tenor.AtmAsk < tenor.AtmBid)
                {
                    result.Errors.Add($"{tenor.CurrencyPair} {tenor.Tenor}: ATM ask < bid (inverted spread)");
                }

                // Kolla RR
                if (Math.Abs(tenor.RR25D) > MaxRR || Math.Abs(tenor.RR10D) > MaxRR)
                {
                    result.Warnings.Add($"{tenor.CurrencyPair} {tenor.Tenor}: RR unusually high");
                }

                // Kolla BF (ska vara positiv)
                if (tenor.BF25D < 0 || tenor.BF10D < 0)
                {
                    result.Errors.Add($"{tenor.CurrencyPair} {tenor.Tenor}: Butterfly is negative");
                }
                else if (tenor.BF25D > MaxBF || tenor.BF10D > MaxBF)
                {
                    result.Warnings.Add($"{tenor.CurrencyPair} {tenor.Tenor}: Butterfly unusually high");
                }

                // Kolla att 10D BF > 25D BF (normalt)
                if (tenor.BF10D < tenor.BF25D && tenor.BF25D > 0)
                {
                    result.Warnings.Add($"{tenor.CurrencyPair} {tenor.Tenor}: 10D BF < 25D BF (unusual)");
                }
            }

            result.IsValid = result.Errors.Count == 0;
            return result;
        }
    }

    public class ValidationResult
    {
        public bool IsValid { get; set; } = true;
        public List<string> Errors { get; } = new();
        public List<string> Warnings { get; } = new();

        public string Summary
        {
            get
            {
                if (IsValid && Warnings.Count == 0)
                    return "All data validated OK";
                if (IsValid)
                    return $"Valid with {Warnings.Count} warning(s)";
                return $"{Errors.Count} error(s), {Warnings.Count} warning(s)";
            }
        }
    }
}