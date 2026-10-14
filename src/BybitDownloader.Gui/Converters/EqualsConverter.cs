using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace BybitDownloader.Gui.Converters;

/// <summary>
/// Two-way equality converter for radio/toggle selection: <c>Convert</c> is true when the bound value equals
/// the parameter; <c>ConvertBack</c> writes the parameter when checked (and leaves the source alone when unchecked).
/// </summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value?.ToString() == parameter?.ToString();

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? parameter?.ToString() ?? BindingOperations.DoNothing : BindingOperations.DoNothing;
}
