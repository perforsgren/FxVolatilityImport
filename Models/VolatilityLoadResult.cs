// Models/VolatilityLoadResult.cs
namespace FxVolatilityImport.Models
{
    /// <summary>
    /// Resultatet av en hämtning från Bloomberg. Saknade värden är double.NaN.
    /// BloombergErrors innehåller security/field-fel som Bloomberg rapporterade (t.ex. okänd ticker).
    /// </summary>
    public sealed record VolatilityLoadResult(
        IReadOnlyList<VolatilityTenor> Data,
        IReadOnlyList<string> BloombergErrors,
        DateTime LoadedAt);
}