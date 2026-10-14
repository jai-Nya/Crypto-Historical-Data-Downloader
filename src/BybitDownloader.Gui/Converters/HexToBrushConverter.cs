using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace BybitDownloader.Gui.Converters;

/// <summary>
/// Converts a <c>#RRGGBB</c>/<c>#AARRGGBB</c> string into a fresh <see cref="SolidColorBrush"/>.
/// A new brush instance per change lets brush transitions animate the swap.
/// </summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string hex && Color.TryParse(hex, out var color)
            ? new SolidColorBrush(color)
            : new SolidColorBrush(Colors.Transparent);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        BindingOperations.DoNothing;
}
