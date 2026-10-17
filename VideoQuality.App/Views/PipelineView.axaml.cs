using Avalonia.Controls;
using Avalonia.Interactivity;
using VideoQuality.App.Services;
using VideoQuality.App.ViewModels;
using VideoQuality.Core.Localization;

namespace VideoQuality.App.Views;

public partial class PipelineView : UserControl
{
    public PipelineView()
    {
        InitializeComponent();
        PageIo.AcceptDrops(this, paths =>
        {
            ViewModel?.AddPaths(paths);
            return Task.CompletedTask;
        });
    }

    private PipelineViewModel? ViewModel => DataContext as PipelineViewModel;

    private async void OnPickVideo(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<string> files = await PageIo.PickFilesAsync(this, Loc.T("pipeline.picker.video"), false);
        if (files.Count > 0 && ViewModel is { } vm)
        {
            vm.SetVideo(files[0]);
        }
    }

    private async void OnPickReference(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<string> files = await PageIo.PickFilesAsync(this, Loc.T("pipeline.picker.reference"), false);
        if (files.Count > 0 && ViewModel is { } vm)
        {
            vm.Reference = files[0];
        }
    }

    private async void OnAddScripts(object? sender, RoutedEventArgs e) =>
        ViewModel?.AddPaths(await PageIo.PickFilesAsync(this, Loc.T("picker.addScripts"), true, (Loc.T("picker.type.scripts"), ["*.ass", "*.ssa"])));

    private async void OnPickFonts(object? sender, RoutedEventArgs e)
    {
        if (await PageIo.PickFolderAsync(this, Loc.T("picker.fontFolder")) is { } folder && ViewModel is { } vm)
        {
            vm.FontDirectory = folder;
        }
    }

    private async void OnPickOutput(object? sender, RoutedEventArgs e)
    {
        if (await PageIo.PickFolderAsync(this, Loc.T("picker.outputFolder")) is { } folder && ViewModel is { } vm)
        {
            vm.OutputDirectory = folder;
        }
    }

    private async void OnPickChapters(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<string> files = await PageIo.PickFilesAsync(this, Loc.T("mux.picker.chapters"), false, (Loc.T("picker.type.chapters"), ["*.xml", "*.txt"]));
        if (files.Count > 0 && ViewModel is { } vm)
        {
            vm.ChaptersFile = files[0];
        }
    }
}
