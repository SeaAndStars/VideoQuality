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
        PageIo.AcceptDrops(this, paths => ViewModel.AddPathsAsync(paths));
    }

    private MuxViewModel ViewModel => (MuxViewModel)DataContext!;

    private async void OnAddFiles(object? sender, RoutedEventArgs e) =>
        await ViewModel.AddPathsAsync(await PageIo.PickFilesAsync(this, Loc.T("mux.picker.add"), true));

    private async void OnAddFonts(object? sender, RoutedEventArgs e)
    {
        if (await PageIo.PickFolderAsync(this, Loc.T("mux.picker.fonts")) is { } folder)
        {
            await ViewModel.AddPathsAsync([folder]);
        }
    }

    private async void OnPickOutput(object? sender, RoutedEventArgs e)
    {
        string suggested = Path.GetFileName(ViewModel.Output) is { Length: > 0 } name ? name : "release.mkv";
        if (await PageIo.PickSaveFileAsync(this, Loc.T("mux.picker.output"), suggested, "mkv") is { } file)
        {
            ViewModel.Output = file;
        }
    }

    private async void OnPickChapters(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<string> files = await PageIo.PickFilesAsync(this, Loc.T("mux.picker.chapters"), false, (Loc.T("picker.type.chapters"), ["*.xml", "*.txt"]));
        if (files.Count > 0)
        {
            ViewModel.Chapters = files[0];
        }
    }
}
