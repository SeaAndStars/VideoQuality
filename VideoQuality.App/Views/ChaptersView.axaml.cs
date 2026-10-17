using Avalonia.Controls;
using Avalonia.Interactivity;
using VideoQuality.App.Services;
using VideoQuality.App.ViewModels;
using VideoQuality.Core.Localization;

namespace VideoQuality.App.Views;

public partial class ChaptersView : UserControl
{
    public ChaptersView()
    {
        InitializeComponent();
        PageIo.AcceptDrops(this, async paths =>
        {
            if (paths.FirstOrDefault(p => !string.IsNullOrEmpty(p)) is { } path)
            {
                await ViewModel.ImportAsync(path);
            }
        });
    }

    private ChaptersViewModel ViewModel => (ChaptersViewModel)DataContext!;

    private async void OnImport(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<string> files = await PageIo.PickFilesAsync(this, Loc.T("chapters.picker.import"), false);
        if (files.Count > 0)
        {
            await ViewModel.ImportAsync(files[0]);
        }
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (await PageIo.PickSaveFileAsync(this, Loc.T("chapters.picker.save"), "chapters.xml", "xml") is { } file)
        {
            ViewModel.Save(file);
        }
    }
}
