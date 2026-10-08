// ViewModels/SurfaceRowViewModel.cs
using FxVolatilityImport.Models;
using FxVolatilityImport.Services;

namespace FxVolatilityImport.ViewModels
{
    /// <summary>En tenor-rad i ytan för det valda valutaparet.</summary>
    public sealed class SurfaceRowViewModel
    {
        public SurfaceRowViewModel(VolatilityTenor tenor, VolatilityTenor? previous, IReadOnlyList<ValidationIssue> issues)
        {
            Tenor = tenor.Tenor;
            Bid = tenor.AtmBid;
            Ask = tenor.AtmAsk;
            Mid = (tenor.AtmBid + tenor.AtmAsk) / 2;
            Spread = tenor.AtmAsk - tenor.AtmBid;
            RR25 = tenor.RR25D;
            RR10 = tenor.RR10D;
            BF25 = tenor.BF25D;
            BF10 = tenor.BF10D;

            if (previous != null)
            {
                var change = Mid - (previous.AtmBid + previous.AtmAsk) / 2;
                MidChange = double.IsFinite(change) ? change : null;
            }

            if (issues.Count > 0)
            {
                IssueLevel = issues.Any(i => i.Severity == IssueSeverity.Error) ? UiLevel.Error : UiLevel.Warning;
                IssueText = string.Join(Environment.NewLine, issues.Select(i => i.Message));
            }
        }

        public string Tenor { get; }
        public double Bid { get; }
        public double Ask { get; }
        public double Mid { get; }
        public double Spread { get; }

        /// <summary>Förändring i ATM mid sedan föregående hämtning (null om ingen jämförelse finns).</summary>
        public double? MidChange { get; }

        public double RR25 { get; }
        public double RR10 { get; }
        public double BF25 { get; }
        public double BF10 { get; }

        public UiLevel IssueLevel { get; } = UiLevel.Neutral;
        public string? IssueText { get; }
        public bool HasIssue => IssueLevel != UiLevel.Neutral;
    }
}