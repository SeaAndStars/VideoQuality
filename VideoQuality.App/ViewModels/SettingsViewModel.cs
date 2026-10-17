using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VideoQuality.Core;
using VideoQuality.Core.Fonts;
using VideoQuality.Core.Localization;
using VideoQuality.Core.Mkv;
using VideoQuality.Core.Settings;

namespace VideoQuality.App.ViewModels;

/// <summary>An entry in the language picker; an empty code follows the system.</summary>
public sealed record LanguageOption(string Code, string Name)
{
    public override string ToString() => Name;
}

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
        LanguageOptions = [new LanguageOption(string.Empty, Loc.T("settings.language.system")),
            .. Loc.Languages.Select(l => new LanguageOption(l.Code, l.NativeName))];
        string saved = compare.PersistedSettings.Language ?? string.Empty;
        SelectedLanguage = LanguageOptions.FirstOrDefault(o => o.Code == saved) ?? LanguageOptions[0];
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
    public partial string MkvToolNixStatus { get; set; } = Loc.T("settings.mkv.looking");

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
                return Loc.T("settings.notAvailable");
            }
        }
    }

    public string Version { get; } = "v" + (typeof(SettingsViewModel).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion
        .Split('+')[0] ?? "dev");

    public IReadOnlyList<LanguageOption> LanguageOptions { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsRestart))]
    public partial LanguageOption SelectedLanguage { get; set; }

    /// <summary>The chosen language differs from the one this process is showing.</summary>
    public bool NeedsRestart => SelectedLanguage is { } option &&
                                (option.Code.Length > 0 ? option.Code : Loc.SystemLanguage) != Loc.Language;

    partial void OnSelectedLanguageChanged(LanguageOption value)
    {
        Compare.UpdateSettings(s => s.Language = value.Code.Length > 0 ? value.Code : null);
    }

    /// <summary>Starts a new instance on the Settings page and closes this one.</summary>
    [RelayCommand]
    private void Restart()
    {
        Compare.FlushSettings();
        string? process = Environment.ProcessPath;
        if (process is null)
        {
            return;
        }

        var start = new ProcessStartInfo(process) { UseShellExecute = false };
        // Under `dotnet VideoQuality.App.dll` the process is the host; pass the app again.
        if (Path.GetFileNameWithoutExtension(process).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
        }

        start.ArgumentList.Add("--page=settings");
        Process.Start(start);
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    partial void OnMkvToolNixFolderChanged(string value)
    {
        Compare.UpdateSettings(s => s.MkvToolNixFolder = string.IsNullOrWhiteSpace(value) ? null : value.Trim());
    }

    [RelayCommand]
    public async Task LocateToolsAsync()
    {
        MkvToolNixStatus = Loc.T("settings.mkv.looking");
        string? folder = string.IsNullOrWhiteSpace(MkvToolNixFolder) ? null : MkvToolNixFolder.Trim();
        MkvToolNix = await MkvToolNix.LocateAsync(folder);
        MkvToolNixStatus = MkvToolNix is { } tools
            ? $"{tools.Version}  ·  {tools.MkvMerge}"
            : Loc.T("settings.mkv.notFound");
    }
}
