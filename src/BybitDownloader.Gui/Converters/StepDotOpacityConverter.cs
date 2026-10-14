using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace BybitDownloader.Gui.Converters;

/// <summary>
/// Maps the current wizard step to a progress-dot opacity: fully filled for steps reached, dim for the rest.
/// The dot index is passed as the converter parameter.
/// </summary>
public sealed class StepDotOpacityConverter : IValueConverter
{
    private const double Reached = 1.0;
    private const double Pending = 70.0 / 255.0;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int step && int.TryParse(parameter?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) && step >= index
            ? Reached
            : Pending;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        BindingOperations.DoNothing;
}
