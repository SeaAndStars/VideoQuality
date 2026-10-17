using Avalonia.Controls;
using Avalonia.Interactivity;
using VideoQuality.App.Services;
using VideoQuality.App.ViewModels;
using VideoQuality.Core.Localization;

namespace VideoQuality.App.Views;

public partial class MuxView : UserControl
{
    public MuxView()
    {
        InitializeComponent();
        PageIo.AcceptDrops(this, async paths =>
        {
            if (ViewModel is { } vm)
            {
                await vm.AddPathsAsync(paths);
            }
        });
    }

    private MuxViewModel? ViewModel => DataContext as MuxViewModel;

    private async void OnAddFiles(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            await vm.AddPathsAsync(await PageIo.PickFilesAsync(this, Loc.T("mux.picker.add"), true));
        }
    }

    private async void OnAddFonts(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && await PageIo.PickFolderAsync(this, Loc.T("mux.picker.fonts")) is { } folder)
        {
            await vm.AddPathsAsync([folder]);
        }
    }

    private async void OnPickOutput(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm &&
            await PageIo.PickSaveFileAsync(this, Loc.T("mux.picker.output"), Path.GetFileName(vm.Output) is { Length: > 0 } n ? n : "release.mkv", "mkv") is { } file)
        {
            vm.Output = file;
        }
    }

    private async void OnPickChapters(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<string> files = await PageIo.PickFilesAsync(this, Loc.T("mux.picker.chapters"), false, (Loc.T("picker.type.chapters"), ["*.xml", "*.txt"]));
        if (files.Count > 0 && ViewModel is { } vm)
        {
            vm.Chapters = files[0];
        }
    }
}
