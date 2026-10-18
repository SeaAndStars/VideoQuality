using System.ComponentModel;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using VideoQuality.Core.Localization;

namespace VideoQuality.App.Localization;

/// <summary>
/// What localized bindings listen to: <see cref="Revision"/> changes whenever the language does, so every
/// text bound through it is read again from the new catalog.
/// </summary>
public sealed class LocSource : INotifyPropertyChanged
{
    public static LocSource Instance { get; } = new();

    private LocSource() => Loc.LanguageChanged += (_, _) =>
    {
        Revision++;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Revision)));
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Counts language changes.</summary>
    public int Revision { get; private set; }

    /// <summary>A binding to <see cref="Revision"/>; its value is irrelevant, only that it changes.</summary>
    internal static Binding RevisionBinding() => new(nameof(Revision)) { Source = Instance, Mode = BindingMode.OneWay };

    /// <summary>The text for a fixed key, kept current.</summary>
    internal static Binding Text(string key) =>
        new(nameof(Revision)) { Source = Instance, Mode = BindingMode.OneWay, Converter = KeyText, ConverterParameter = key };

    private static readonly IValueConverter KeyText = new FuncValueConverter<int, string, string>((_, key) => Loc.T(key!));
}
