// Models/VolTenors.cs
namespace FxVolatilityImport.Models
{
    /// <summary>
    /// Tenorerna som hämtas från Bloomberg och skickas till MX3 (i denna ordning).
    /// </summary>
    public static class VolTenors
    {
        public static readonly IReadOnlyList<string> All = new[]
        {
            "ON", "1W", "2W", "1M", "2M", "3M", "6M", "1Y", "2Y", "3Y"
        };
    }
}