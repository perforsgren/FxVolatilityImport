// Services/DataValidator.cs
using FxVolatilityImport.Models;

namespace FxVolatilityImport.Services
{
    public enum IssueSeverity { Warning, Error }

    public sealed record ValidationIssue(string CurrencyPair, string Tenor, IssueSeverity Severity, string Message);

    /// <summary>
    /// Rimlighetskontroll av Bloomberg-data före export.
    /// Fel (Error) gör att HELA valutaparet utesluts ur importen – övriga par importeras ändå.
    /// Varningar (Warning) visas men stoppar inget.
    /// </summary>
    public class DataValidator
    {
        private const double MinAtmVol = 0.1;    // under detta är suspekt (EURDKK ligger normalt runt 0.3–1.0)
        private const double MaxAtmVol = 100.0;  // över detta är suspekt
        private const double MaxRR = 20.0;       // |RR| > 20 är suspekt
        private const double MaxBF = 10.0;       // BF > 10 är suspekt

        public ValidationResult Validate(IEnumerable<VolatilityTenor> data)
        {
            var issues = new List<ValidationIssue>();

            foreach (var t in data)
            {
                void Error(string message) => issues.Add(new ValidationIssue(t.CurrencyPair, t.Tenor, IssueSeverity.Error, message));
                void Warning(string message) => issues.Add(new ValidationIssue(t.CurrencyPair, t.Tenor, IssueSeverity.Warning, message));

                var missing = new List<string>();
                if (!double.IsFinite(t.AtmBid)) missing.Add("ATM bid");
                if (!double.IsFinite(t.AtmAsk)) missing.Add("ATM ask");
                if (!double.IsFinite(t.RR25D)) missing.Add("RR 25D");
                if (!double.IsFinite(t.RR10D)) missing.Add("RR 10D");
                if (!double.IsFinite(t.BF25D)) missing.Add("BF 25D");
                if (!double.IsFinite(t.BF10D)) missing.Add("BF 10D");
                if (missing.Count > 0)
                {
                    Error($"Missing from Bloomberg: {string.Join(", ", missing)}");
                    continue;
                }

                // ATM
                if (t.AtmBid <= 0 || t.AtmAsk <= 0)
                    Error($"ATM bid/ask is zero or negative ({t.AtmBid:F3}/{t.AtmAsk:F3})");
                else if (t.AtmBid < MinAtmVol || t.AtmAsk < MinAtmVol)
                    Warning($"ATM unusually low ({t.AtmBid:F3}/{t.AtmAsk:F3})");
                else if (t.AtmBid > MaxAtmVol || t.AtmAsk > MaxAtmVol)
                    Warning($"ATM unusually high ({t.AtmBid:F3}/{t.AtmAsk:F3})");

                if (t.AtmAsk < t.AtmBid)
                    Error($"ATM ask < bid ({t.AtmBid:F3}/{t.AtmAsk:F3})");

                // Risk reversals
                if (Math.Abs(t.RR25D) > MaxRR || Math.Abs(t.RR10D) > MaxRR)
                    Warning($"RR unusually large (25D {t.RR25D:F3}, 10D {t.RR10D:F3})");

                // Butterflies (ska vara positiva)
                if (t.BF25D < 0 || t.BF10D < 0)
                    Error($"Negative butterfly (25D {t.BF25D:F3}, 10D {t.BF10D:F3})");
                else if (t.BF25D > MaxBF || t.BF10D > MaxBF)
                    Warning($"Butterfly unusually large (25D {t.BF25D:F3}, 10D {t.BF10D:F3})");

                if (t.BF10D < t.BF25D && t.BF25D > 0)
                    Warning($"10D BF below 25D BF ({t.BF10D:F3} < {t.BF25D:F3})");
            }

            return new ValidationResult(issues);
        }
    }

    public sealed class ValidationResult
    {
        public static readonly ValidationResult Empty = new(Array.Empty<ValidationIssue>());

        public ValidationResult(IReadOnlyList<ValidationIssue> issues)
        {
            Issues = issues;
            PairsWithErrors = issues
                .Where(i => i.Severity == IssueSeverity.Error)
                .Select(i => i.CurrencyPair)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        public IReadOnlyList<ValidationIssue> Issues { get; }

        /// <summary>Par som har minst ett fel och därför utesluts ur exporten.</summary>
        public IReadOnlySet<string> PairsWithErrors { get; }

        public int ErrorCount => Issues.Count(i => i.Severity == IssueSeverity.Error);
        public int WarningCount => Issues.Count(i => i.Severity == IssueSeverity.Warning);
        public bool IsValid => PairsWithErrors.Count == 0;

        public IReadOnlyList<ValidationIssue> For(string currencyPair)
            => Issues.Where(i => i.CurrencyPair.Equals(currencyPair, StringComparison.OrdinalIgnoreCase)).ToList();
    }
}