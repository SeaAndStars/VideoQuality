using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace VideoQuality.App.Converters;

/// <summary>
/// For two-way <c>SelectedItem</c> bindings: a combo box briefly selects nothing while its items
/// change; that transient <c>null</c> must not be written into the view model (it would also fail
/// for enum and number properties).
/// </summary>
public sealed class KeepSelectionConverter : IValueConverter
{
    public static KeepSelectionConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value ?? BindingOperations.DoNothing;
}
