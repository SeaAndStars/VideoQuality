using Avalonia.Controls;
using Avalonia.Interactivity;
using VideoQuality.App.Services;
using VideoQuality.App.ViewModels;
using VideoQuality.Core.Localization;

namespace VideoQuality.App.Views;

public partial class TrimMergeView : UserControl
{
    public TrimMergeView()
    {
        InitializeComponent();
        // Each card takes its own drops: a video on Cut becomes the source, files on Join are appended.
        PageIo.AcceptDrops(CutCard, async paths =>
        {
            if (paths.FirstOrDefault(p => !string.IsNullOrEmpty(p)) is { } path)
            {
                await ViewModel.OpenCutSourceAsync(path);
            }
        });
        PageIo.AcceptDrops(MergeCard, paths => ViewModel.AddMergeInputsAsync(paths));
    }

    private TrimMergeViewModel ViewModel => (TrimMergeViewModel)DataContext!;

    private async void OnPickCutSource(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<string> files = await PageIo.PickFilesAsync(this, Loc.T("extract.picker.open"), false);
        if (files.Count > 0)
        {
            await ViewModel.OpenCutSourceAsync(files[0]);
        }
    }

    private async void OnAddMergeFiles(object? sender, RoutedEventArgs e) =>
        await ViewModel.AddMergeInputsAsync(await PageIo.PickFilesAsync(this, Loc.T("trim.picker.parts"), true));

    private async void OnPickMergeOutput(object? sender, RoutedEventArgs e)
    {
        string suggested = Path.GetFileName(ViewModel.MergeOutput) is { Length: > 0 } name ? name : "joined.mkv";
        if (await PageIo.PickSaveFileAsync(this, Loc.T("trim.picker.output"), suggested, "mkv") is { } file)
        {
            ViewModel.MergeOutput = file;
        }
    }
}
