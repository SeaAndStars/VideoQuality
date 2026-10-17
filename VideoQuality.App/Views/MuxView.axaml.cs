using Avalonia.Controls;
using Avalonia.Interactivity;
using VideoQuality.App.Services;
using VideoQuality.App.ViewModels;

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
            await vm.AddPathsAsync(await PageIo.PickFilesAsync(this, "Add video, audio or subtitles", true));
        }
    }

    private async void OnAddFonts(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && await PageIo.PickFolderAsync(this, "Folder with the fonts to attach") is { } folder)
        {
            await vm.AddPathsAsync([folder]);
        }
    }

    private async void OnPickOutput(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm &&
            await PageIo.PickSaveFileAsync(this, "Save the MKV as", Path.GetFileName(vm.Output) is { Length: > 0 } n ? n : "release.mkv", "mkv") is { } file)
        {
            vm.Output = file;
        }
    }

    private async void OnPickChapters(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<string> files = await PageIo.PickFilesAsync(this, "Chapters file", false, ("Matroska chapters", ["*.xml", "*.txt"]));
        if (files.Count > 0 && ViewModel is { } vm)
        {
            vm.Chapters = files[0];
        }
    }
}
