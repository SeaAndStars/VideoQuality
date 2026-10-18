using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FluentIcons.Common;
using VideoQuality.App.Services;
using VideoQuality.Core.Encode;
using VideoQuality.Core.Ingest;
using VideoQuality.Core.Localization;
using VideoQuality.Core.Monitor;

namespace VideoQuality.App.ViewModels;

/// <summary>One entry of the navigation rail; a separator divides the groups.</summary>
public sealed record NavPage(string Key, string Title, Symbol Icon, object? ViewModel, bool IsSeparator = false)
{
    public static NavPage Separator(string group) => new("-" + group, string.Empty, Symbol.LineHorizontal1, null, true);
}

/// <summary>
/// The window: navigation between the toolkit's pages, grouped as inspect (检查), make (制作) and
/// automate (自动). Page view models live for the whole session, so switching pages never loses work
/// and long jobs keep running in the background.
/// </summary>
public partial class ShellViewModel : ObservableObject, IDisposable
{
    public ShellViewModel(MainViewModel compare, AppServices? services = null)
    {
        Services = services ?? AppServices.Default;
        Compare = compare;
        Settings = new SettingsViewModel(compare, Services);
        Queue = new EncodeQueue(Services.Processes, () => Settings.Encoder?.Path ?? "ffmpeg");
        MediaInfo = new MediaInfoViewModel();
        FontSubset = new FontSubsetViewModel();
        Extract = new ExtractViewModel(Settings);
        Encode = new EncodeViewModel(Settings, Queue, new PresetStore(PresetStore.DefaultDirectory));
        TrimMerge = new TrimMergeViewModel(Settings, Encode);
        Chapters = new ChaptersViewModel();
        Mux = new MuxViewModel(Settings);
        Naming = new NamingViewModel();
        Pipeline = new PipelineViewModel(Settings, compare, Queue, Naming);
        Monitor = new MonitorViewModel(new SystemMonitor(Services.Processes), Queue);

        Pages =
        [
            new NavPage("mediainfo", Loc.T("nav.mediainfo"), Symbol.Info, MediaInfo),
            new NavPage("compare", Loc.T("nav.compare"), Symbol.DataLine, Compare),
            NavPage.Separator("make"),
            new NavPage("extract", Loc.T("nav.extract"), Symbol.ArrowExportUp, Extract),
            new NavPage("encode", Loc.T("nav.encode"), Symbol.VideoClip, Encode),
            new NavPage("trim", Loc.T("nav.trim"), Symbol.Cut, TrimMerge),
            new NavPage("subset", Loc.T("nav.subset"), Symbol.TextFont, FontSubset),
            new NavPage("chapters", Loc.T("nav.chapters"), Symbol.BookOpen, Chapters),
            new NavPage("mux", Loc.T("nav.mux"), Symbol.Merge, Mux),
            new NavPage("naming", Loc.T("nav.naming"), Symbol.Rename, Naming),
            NavPage.Separator("automate"),
            new NavPage("pipeline", Loc.T("nav.pipeline"), Symbol.FlowchartCircle, Pipeline),
            new NavPage("monitor", Loc.T("nav.monitor"), Symbol.Pulse, Monitor),
        ];
        SettingsPage = new NavPage("settings", Loc.T("nav.settings"), Symbol.Settings, Settings);
        SelectedPage = Pages.First(p => p.Key == "compare");

        Compare.PropertyChanged += OnComparePropertyChanged;

        // The shared toast surface.
        FontSubset.ToastRequested += (_, t) => ToastHost.Show(t);
        Extract.ToastRequested += (_, t) => ToastHost.Show(t);
        Mux.ToastRequested += (_, t) => ToastHost.Show(t);
        Pipeline.ToastRequested += (_, t) => ToastHost.Show(t);
        Encode.ToastRequested += (_, t) => ToastHost.Show(t);
        TrimMerge.ToastRequested += (_, t) => ToastHost.Show(t);

        // Page-to-page hand-offs.
        FontSubset.SendToMuxRequested += async (_, output) =>
        {
            Navigate("mux");
            await Mux.AddSubsetOutputAsync(output.Scripts, output.Fonts);
        };
        Chapters.SendToMuxRequested += (_, path) =>
        {
            Mux.Chapters = path;
            Navigate("mux");
        };
        Encode.CompareRequested += async (_, pair) =>
        {
            Navigate("compare");
            await Compare.HandleDropAsync([pair.Source, pair.Output], DropZone.Reference);
        };
        Encode.RevealRequested += (_, path) => RevealRequested?.Invoke(this, path);
        Pipeline.RevealRequested += (_, path) => RevealRequested?.Invoke(this, path);
        Pipeline.CompareRequested += (_, _) => Navigate("compare");
    }

    public AppServices Services { get; }

    public EncodeQueue Queue { get; }

    public MainViewModel Compare { get; }

    public MediaInfoViewModel MediaInfo { get; }

    public FontSubsetViewModel FontSubset { get; }

    public ExtractViewModel Extract { get; }

    public EncodeViewModel Encode { get; }

    public TrimMergeViewModel TrimMerge { get; }

    public ChaptersViewModel Chapters { get; }

    public MuxViewModel Mux { get; }

    public NamingViewModel Naming { get; }

    public PipelineViewModel Pipeline { get; }

    public MonitorViewModel Monitor { get; }

    public SettingsViewModel Settings { get; }

    public IReadOnlyList<NavPage> Pages { get; }

    public NavPage SettingsPage { get; }

    /// <summary>Asks the window to show a file in its folder.</summary>
    public event EventHandler<string>? RevealRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TitleText), nameof(WindowTitle), nameof(PageKey), nameof(IsCompare), nameof(IsSettings))]
    public partial NavPage? SelectedPage { get; set; }

    /// <summary>Rail shows icons only (narrow windows or user choice).</summary>
    [ObservableProperty]
    public partial bool IsNavCollapsed { get; set; }

    /// <summary>The visible page's key; each page view is shown when it matches.</summary>
    public string PageKey => SelectedPage?.Key ?? string.Empty;

    public bool IsCompare => PageKey == "compare";

    public bool IsSettings => PageKey == "settings";

    /// <summary>Centre of the title bar: the comparison on Compare, the page name elsewhere.</summary>
    public string TitleText => IsCompare && Compare.ComparisonTitle.Length > 0
        ? Compare.ComparisonTitle
        : SelectedPage?.Title ?? string.Empty;

    public string WindowTitle => IsCompare ? Compare.WindowTitle : $"{SelectedPage?.Title} — VideoQuality";

    // A separator cannot be selected: keep the page that was showing.
    partial void OnSelectedPageChanging(NavPage? oldValue, NavPage? newValue)
    {
        if (newValue is { IsSeparator: true })
        {
            _revertTo = oldValue;
        }
    }

    private NavPage? _revertTo;

    partial void OnSelectedPageChanged(NavPage? value)
    {
        if (value is { IsSeparator: true })
        {
            SelectedPage = _revertTo;
        }
    }

    public void Navigate(string key) =>
        SelectedPage = Pages.FirstOrDefault(p => p.Key == key && !p.IsSeparator) ?? (key == "settings" ? SettingsPage : SelectedPage);

    /// <summary>
    /// Hands startup files ("Open With", command line) to a page as if they were dropped on it.
    /// Returns false for pages that don't take files, so the caller can fall back to Compare.
    /// </summary>
    public async Task<bool> OpenFilesAsync(string key, IReadOnlyList<string?> files)
    {
        if (key is "extract" or "mux" or "encode" or "trim" && Settings.MkvToolNix is null)
        {
            await Settings.LocateToolsAsync();
        }

        string? first = files.FirstOrDefault(f => !string.IsNullOrEmpty(f));
        switch (key)
        {
            case "subset":
                FontSubset.AddPaths(files);
                break;
            case "extract" when first is not null:
                await Extract.OpenAsync(first);
                break;
            case "mux":
                await Mux.AddPathsAsync(files);
                break;
            case "pipeline":
                Pipeline.AddPaths(files);
                break;
            case "encode":
                await Encode.AddPathsAsync(files);
                break;
            case "mediainfo" when first is not null:
                await MediaInfo.OpenAsync(first);
                break;
            case "trim" when first is not null:
                await TrimMerge.OpenCutSourceAsync(first);
                break;
            case "chapters" when first is not null:
                await Chapters.ImportAsync(first);
                break;
            case "naming" when first is not null:
                await Naming.FillFromAsync(first);
                break;
            default:
                return false;
        }

        Navigate(key);
        return true;
    }

    private void OnComparePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.ComparisonTitle) or nameof(MainViewModel.WindowTitle))
        {
            OnPropertyChanged(nameof(TitleText));
            OnPropertyChanged(nameof(WindowTitle));
        }
    }

    public void Dispose()
    {
        Compare.PropertyChanged -= OnComparePropertyChanged;
        foreach (EncodeJob job in Queue.Jobs)
        {
            Queue.Stop(job);
        }

        Compare.FlushSettings();
        Compare.Dispose();
        GC.SuppressFinalize(this);
    }
}
