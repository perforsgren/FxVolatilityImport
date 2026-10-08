// Converters/Converters.cs
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using FxVolatilityImport.Services;
using FxVolatilityImport.ViewModels;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace FxVolatilityImport.Converters
{
    /// <summary>
    /// UiLevel / LogLevel / bool → pensel från Theme.xaml.
    /// ConverterParameter: "Fg" (text/prick), "Bg" (svag bakgrund) eller "Border".
    /// </summary>
    public sealed class LevelToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        {
            var level = value switch
            {
                UiLevel ui => ui,
                LogLevel log => log switch
                {
                    LogLevel.Success => UiLevel.Good,
                    LogLevel.Warning => UiLevel.Warning,
                    LogLevel.Error => UiLevel.Error,
                    _ => UiLevel.Info
                },
                bool b => b ? UiLevel.Good : UiLevel.Error,
                _ => UiLevel.Neutral
            };

            var part = parameter as string ?? "Fg";
            return Application.Current?.TryFindResource($"Level.{level}.{part}") as Brush ?? Brushes.Gray;
        }

        public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>double/double? → text med givet format (InvariantCulture). NaN/null visas som "–".</summary>
    public sealed class NumberConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        {
            var format = parameter as string ?? "0.000";
            return value switch
            {
                double d when double.IsFinite(d) => d.ToString(format, CultureInfo.InvariantCulture),
                _ => "–"
            };
        }

        public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>Förändring → färg: upp grön, ned röd, oförändrad/saknas dämpad.</summary>
    public sealed class ChangeToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        {
            var key = value is double d && double.IsFinite(d) && Math.Abs(d) >= 0.0005
                ? (d > 0 ? "Level.Good.Fg" : "Level.Error.Fg")
                : "Brush.TextFaint";
            return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
        }

        public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>bool → Visibility. ConverterParameter="Invert" vänder på logiken.</summary>
    public sealed class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        {
            var flag = value is bool b && b;
            if (string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase))
                flag = !flag;
            return flag ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>Tom/null-sträng → Collapsed.</summary>
    public sealed class EmptyToCollapsedConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
            => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}