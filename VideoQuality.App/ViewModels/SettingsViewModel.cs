using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VideoQuality.Core;
using VideoQuality.Core.Fonts;
using VideoQuality.Core.Mkv;
using VideoQuality.Core.Settings;

namespace VideoQuality.App.ViewModels;

/// <summary>
/// App-wide settings and tool detection: scoring options (shared with Compare), ffmpeg, and the
/// mkvtoolnix installation the Extract, Mux and Pipeline pages use.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    public SettingsViewModel(MainViewModel compare)
    {
        Compare = compare;
        MkvToolNixFolder = compare.PersistedSettings.MkvToolNixFolder ?? string.Empty;
        _ = LocateToolsAsync();
    }

    /// <summary>Model, backend and ffmpeg path live on the Compare view model; bound directly.</summary>
    public MainViewModel Compare { get; }

    [ObservableProperty]
    public partial string MkvToolNixFolder { get; set; }

    /// <summary>The located mkvtoolnix, or null; pages enable their actions from this.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMkvToolNix))]
    public partial MkvToolNix? MkvToolNix { get; set; }

    [ObservableProperty]
    public partial string MkvToolNixStatus { get; set; } = "Looking for mkvtoolnix…";

    public bool HasMkvToolNix => MkvToolNix is not null;

    public string SettingsPath => SettingsFile.CurrentPath ?? SettingsFile.PortablePath;

    public string AppFolder => AppPaths.Root;

    public string HarfBuzzVersion
    {
        get
        {
            try
            {
                return HarfBuzzSubset.Version;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return "not available";
            }
        }
    }

    public string Version { get; } = "v" + (typeof(SettingsViewModel).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion
        .Split('+')[0] ?? "dev");

    partial void OnMkvToolNixFolderChanged(string value)
    {
        Compare.UpdateSettings(s => s.MkvToolNixFolder = string.IsNullOrWhiteSpace(value) ? null : value.Trim());
    }

    [RelayCommand]
    public async Task LocateToolsAsync()
    {
        MkvToolNixStatus = "Looking for mkvtoolnix…";
        string? folder = string.IsNullOrWhiteSpace(MkvToolNixFolder) ? null : MkvToolNixFolder.Trim();
        MkvToolNix = await MkvToolNix.LocateAsync(folder);
        MkvToolNixStatus = MkvToolNix is { } tools
            ? $"{tools.Version}  ·  {tools.MkvMerge}"
            : "Not found. Install MKVToolNix (mkvtoolnix.download) or point to its folder.";
    }
}
