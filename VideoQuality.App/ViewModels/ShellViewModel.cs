using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FluentIcons.Common;

namespace VideoQuality.App.ViewModels;

/// <summary>One entry of the navigation rail.</summary>
public sealed record NavPage(string Key, string Title, Symbol Icon, object ViewModel);

/// <summary>
/// The window: navigation between the toolkit's pages. Page view models live for the whole session,
/// so switching pages never loses work and long jobs keep running in the background.
/// </summary>
public partial class ShellViewModel : ObservableObject, IDisposable
{
    public ShellViewModel(MainViewModel compare)
    {
        Compare = compare;
        Settings = new SettingsViewModel(compare);
        FontSubset = new FontSubsetViewModel();
        Extract = new ExtractViewModel(Settings);
        Mux = new MuxViewModel(Settings);
        Pipeline = new PipelineViewModel(Settings, compare);

        Pages =
        [
            new NavPage("compare", "Compare", Symbol.DataLine, Compare),
            new NavPage("subset", "Font Subset", Symbol.TextFont, FontSubset),
            new NavPage("extract", "Extract", Symbol.ArrowExportUp, Extract),
            new NavPage("mux", "Mux", Symbol.Merge, Mux),
            new NavPage("pipeline", "Pipeline", Symbol.FlowchartCircle, Pipeline),
        ];
        SettingsPage = new NavPage("settings", "Settings", Symbol.Settings, Settings);
        SelectedPage = Pages[0];

        Compare.PropertyChanged += OnComparePropertyChanged;

        // Page-to-page hand-offs and the shared toast surface.
        FontSubset.ToastRequested += (_, t) => Services.ToastHost.Show(t);
        Extract.ToastRequested += (_, t) => Services.ToastHost.Show(t);
        Mux.ToastRequested += (_, t) => Services.ToastHost.Show(t);
        Pipeline.ToastRequested += (_, t) => Services.ToastHost.Show(t);
        FontSubset.SendToMuxRequested += async (_, output) =>
        {
            Navigate("mux");
            await Mux.AddSubsetOutputAsync(output.Scripts, output.Fonts);
        };
        Pipeline.CompareRequested += (_, _) => Navigate("compare");
    }

    public MainViewModel Compare { get; }

    public FontSubsetViewModel FontSubset { get; }

    public ExtractViewModel Extract { get; }

    public MuxViewModel Mux { get; }

    public PipelineViewModel Pipeline { get; }

    public SettingsViewModel Settings { get; }

    public IReadOnlyList<NavPage> Pages { get; }

    public NavPage SettingsPage { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TitleText), nameof(WindowTitle), nameof(IsCompare), nameof(IsFontSubset),
        nameof(IsExtract), nameof(IsMux), nameof(IsPipeline), nameof(IsSettings))]
    public partial NavPage? SelectedPage { get; set; }

    /// <summary>Rail shows icons only (narrow windows or user choice).</summary>
    [ObservableProperty]
    public partial bool IsNavCollapsed { get; set; }

    public bool IsCompare => SelectedPage?.Key == "compare";

    public bool IsFontSubset => SelectedPage?.Key == "subset";

    public bool IsExtract => SelectedPage?.Key == "extract";

    public bool IsMux => SelectedPage?.Key == "mux";

    public bool IsPipeline => SelectedPage?.Key == "pipeline";

    public bool IsSettings => SelectedPage?.Key == "settings";

    /// <summary>Centre of the title bar: the comparison on Compare, the page name elsewhere.</summary>
    public string TitleText => IsCompare && Compare.ComparisonTitle.Length > 0
        ? Compare.ComparisonTitle
        : SelectedPage?.Title ?? string.Empty;

    public string WindowTitle => IsCompare ? Compare.WindowTitle : $"{SelectedPage?.Title} — VideoQuality";

    public void Navigate(string key) =>
        SelectedPage = Pages.FirstOrDefault(p => p.Key == key) ?? (key == "settings" ? SettingsPage : SelectedPage);

    /// <summary>
    /// Hands startup files ("Open With", command line) to a page as if they were dropped on it.
    /// Returns false for pages that don't take files, so the caller can fall back to Compare.
    /// </summary>
    public async Task<bool> OpenFilesAsync(string key, IReadOnlyList<string?> files)
    {
        if (key is "extract" or "mux" && Settings.MkvToolNix is null)
        {
            await Settings.LocateToolsAsync();
        }

        switch (key)
        {
            case "subset":
                FontSubset.AddPaths(files);
                break;
            case "extract" when files.FirstOrDefault(f => !string.IsNullOrEmpty(f)) is { } first:
                await Extract.OpenAsync(first);
                break;
            case "mux":
                await Mux.AddPathsAsync(files);
                break;
            case "pipeline":
                Pipeline.AddPaths(files);
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
        Compare.FlushSettings();
        Compare.Dispose();
    }
}
