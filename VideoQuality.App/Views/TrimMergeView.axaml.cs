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
            if (paths.FirstOrDefault(p => !string.IsNullOrEmpty(p)) is { } path && ViewModel is { } vm)
            {
                await vm.OpenCutSourceAsync(path);
            }
        });
        PageIo.AcceptDrops(MergeCard, async paths =>
        {
            if (ViewModel is { } vm)
            {
                await vm.AddMergeInputsAsync(paths);
            }
        });
    }

    private TrimMergeViewModel? ViewModel => DataContext as TrimMergeViewModel;

    private async void OnPickCutSource(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<string> files = await PageIo.PickFilesAsync(this, Loc.T("extract.picker.open"), false);
        if (files.Count > 0 && ViewModel is { } vm)
        {
            await vm.OpenCutSourceAsync(files[0]);
        }
    }

    private async void OnAddMergeFiles(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            await vm.AddMergeInputsAsync(await PageIo.PickFilesAsync(this, Loc.T("trim.picker.parts"), true));
        }
    }

    private async void OnPickMergeOutput(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm &&
            await PageIo.PickSaveFileAsync(this, Loc.T("trim.picker.output"), Path.GetFileName(vm.MergeOutput) is { Length: > 0 } n ? n : "joined.mkv", "mkv") is { } file)
        {
            vm.MergeOutput = file;
        }
    }
}
