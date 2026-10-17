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
            if (paths.FirstOrDefault(p => !string.IsNullOrEmpty(p)) is { } path && ViewModel is { } vm)
            {
                await vm.ImportAsync(path);
            }
        });
    }

    private ChaptersViewModel? ViewModel => DataContext as ChaptersViewModel;

    private async void OnImport(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<string> files = await PageIo.PickFilesAsync(this, Loc.T("chapters.picker.import"), false);
        if (files.Count > 0 && ViewModel is { } vm)
        {
            await vm.ImportAsync(files[0]);
        }
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && await PageIo.PickSaveFileAsync(this, Loc.T("chapters.picker.save"), "chapters.xml", "xml") is { } file)
        {
            vm.Save(file);
        }
    }
}
