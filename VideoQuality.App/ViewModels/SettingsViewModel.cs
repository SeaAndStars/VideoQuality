using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VideoQuality.Core;
using VideoQuality.Core.Fonts;
using VideoQuality.Core.Localization;
using VideoQuality.Core.Mkv;
using VideoQuality.Core.Encode;
using VideoQuality.Core.Settings;
using VideoQuality.Core.Tools;
using VideoQuality.App.Services;

namespace VideoQuality.App.ViewModels;

/// <summary>An entry in the language picker; an empty code follows the system.</summary>
/// <summary>A UI language; the empty code follows the system.</summary>
public sealed record LanguageOption(string Code, string NativeName)
{
    /// <summary>The language in its own script, or "follow the system" in the current language.</summary>
    public string Name => Code.Length > 0 ? NativeName : Loc.T("settings.language.system");

    public override string ToString() => Name;
}

/// <summary>
/// App-wide settings and tool detection: scoring options (shared with Compare), ffmpeg, and the
/// mkvtoolnix installation the Extract, Mux and Pipeline pages use.
/// </summary>
public partial class SettingsViewModel : ViewModelBase
{
    public SettingsViewModel(MainViewModel compare, AppServices? services = null)
    {
        Compare = compare;
        Services = services ?? AppServices.Default;
        MkvToolNixFolder = compare.PersistedSettings.MkvToolNixFolder ?? string.Empty;
        EncoderFolder = compare.PersistedSettings.EncoderFolder ?? string.Empty;
        TrackNaming = Enum.TryParse(compare.PersistedSettings.TrackNaming, out TrackNamingProfile profile) ? profile : TrackNamingProfile.GreenTea;
        EncodeConcurrency = Math.Clamp(compare.PersistedSettings.EncodeConcurrency, 1, 4);
        LanguageOptions = [new LanguageOption(string.Empty, string.Empty),
            .. Loc.Languages.Select(l => new LanguageOption(l.Code, l.NativeName))];
        string saved = compare.PersistedSettings.Language ?? string.Empty;
        SelectedLanguage = LanguageOptions.FirstOrDefault(o => o.Code == saved) ?? LanguageOptions[0];
        _languageReady = true; // the app applied the saved language at startup
        _ = LocateToolsAsync();
    }

    public AppServices Services { get; }

    /// <summary>Model, backend and ffmpeg path live on the Compare view model; bound directly.</summary>
    public MainViewModel Compare { get; }

    [ObservableProperty]
    public partial string MkvToolNixFolder { get; set; }

    /// <summary>The located mkvtoolnix, or null; pages enable their actions from this.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMkvToolNix))]
    public partial MkvToolNix? MkvToolNix { get; set; }

    [ObservableProperty]
    public partial string MkvToolNixStatus { get; set; } = Loc.T("settings.mkv.looking");

    public bool HasMkvToolNix => MkvToolNix is not null;

    public string SettingsPath => SettingsFile.Location;

    public string AppFolder => AppPaths.Root;

    public string HarfBuzzVersion => NativeVersion(() => HarfBuzzSubset.Version);

    /// <summary>A native library's version, or "not available" when it cannot be loaded.</summary>
    internal static string NativeVersion(Func<string> version)
    {
        try
        {
            return version();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return Loc.T("settings.notAvailable");
        }
    }

    public string Version => AppInfo.Version;

    public IReadOnlyList<LanguageOption> LanguageOptions { get; }

    /// <summary>The UI language; a change applies at once, to every open page.</summary>
    [ObservableProperty]
    public partial LanguageOption SelectedLanguage { get; set; }

    private readonly bool _languageReady;

    partial void OnSelectedLanguageChanged(LanguageOption value)
    {
        if (_languageReady)
        {
            Compare.UpdateSettings(s => s.Language = value.Code.Length > 0 ? value.Code : null);
            Loc.SetLanguage(value.Code);
        }
    }

    public IReadOnlyList<TrackNamingProfile> TrackNamingOptions { get; } = Enum.GetValues<TrackNamingProfile>();

    /// <summary>How Mux and Pipeline tag tracks (GreenTea rules by default).</summary>
    [ObservableProperty]
    public partial TrackNamingProfile TrackNaming { get; set; }

    partial void OnTrackNamingChanged(TrackNamingProfile value) =>
        Compare.UpdateSettings(s => s.TrackNaming = value.ToString());

    public IReadOnlyList<int> ConcurrencyOptions { get; } = [1, 2, 3, 4];

    [ObservableProperty]
    public partial int EncodeConcurrency { get; set; }

    partial void OnEncodeConcurrencyChanged(int value) =>
        Compare.UpdateSettings(s => s.EncodeConcurrency = value);

    [ObservableProperty]
    public partial string EncoderFolder { get; set; }

    /// <summary>The encoder ffmpeg (GPL build: x264/x265/libass …), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEncoder))]
    public partial EncoderTool? Encoder { get; set; }

    [ObservableProperty]
    public partial string EncoderStatus { get; set; } = Loc.T("settings.encoder.looking");

    public bool HasEncoder => Encoder is not null;

    partial void OnEncoderFolderChanged(string value) =>
        Compare.UpdateSettings(s => s.EncoderFolder = string.IsNullOrWhiteSpace(value) ? null : value.Trim());

    partial void OnMkvToolNixFolderChanged(string value)
    {
        Compare.UpdateSettings(s => s.MkvToolNixFolder = string.IsNullOrWhiteSpace(value) ? null : value.Trim());
    }

    [RelayCommand]
    public async Task LocateToolsAsync()
    {
        MkvToolNixStatus = Loc.T("settings.mkv.looking");
        string? folder = string.IsNullOrWhiteSpace(MkvToolNixFolder) ? null : MkvToolNixFolder.Trim();
        MkvToolNix = await MkvToolNix.LocateAsync(folder, Services.Processes);
        MkvToolNixStatus = MkvToolNix is { } tools
            ? $"{tools.Version}  ·  {tools.MkvMerge}"
            : Loc.T("settings.mkv.notFound");

        EncoderStatus = Loc.T("settings.encoder.looking");
        string? encoderFolder = string.IsNullOrWhiteSpace(EncoderFolder) ? null : EncoderFolder.Trim();
        Encoder = await EncoderTool.LocateAsync(encoderFolder, Services.Processes);
        EncoderStatus = Encoder is { } encoder
            ? Loc.T("settings.encoder.found", encoder.Version, encoder.Path,
                string.Join(", ", EncoderCatalog.AvailableVideo(encoder).Where(e => e.Name != "copy").Select(e => e.Name)))
            : Loc.T("settings.encoder.notFound");
    }
}
