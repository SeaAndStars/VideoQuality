using Avalonia.Data.Converters;

namespace VideoQuality.App.Converters;

/// <summary>Lists show file names; the full path goes in the tooltip.</summary>
public static class PathConverters
{
    public static readonly FuncValueConverter<string?, string?> FileName = new(path => Path.GetFileName(path));
}
