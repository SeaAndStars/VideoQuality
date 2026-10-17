using Avalonia.Controls;
using Avalonia.Interactivity;
using VideoQuality.App.Services;
using VideoQuality.App.ViewModels;

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
        IReadOnlyList<string> files = await PageIo.PickFilesAsync(this, "Encoded video", false);
        if (files.Count > 0 && ViewModel is { } vm)
        {
            vm.Video = files[0];
        }
    }

    private async void OnPickReference(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<string> files = await PageIo.PickFilesAsync(this, "Reference video", false);
        if (files.Count > 0 && ViewModel is { } vm)
        {
            vm.Reference = files[0];
        }
    }

    private async void OnAddScripts(object? sender, RoutedEventArgs e) =>
        ViewModel?.AddPaths(await PageIo.PickFilesAsync(this, "Add ASS scripts", true, ("ASS/SSA scripts", ["*.ass", "*.ssa"])));

    private async void OnPickFonts(object? sender, RoutedEventArgs e)
    {
        if (await PageIo.PickFolderAsync(this, "Folder with the fonts") is { } folder && ViewModel is { } vm)
        {
            vm.FontDirectory = folder;
        }
    }

    private async void OnPickOutput(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm &&
            await PageIo.PickSaveFileAsync(this, "Save the release as", Path.GetFileName(vm.Output) is { Length: > 0 } n ? n : "release.mkv", "mkv") is { } file)
        {
            vm.Output = file;
        }
    }
}
