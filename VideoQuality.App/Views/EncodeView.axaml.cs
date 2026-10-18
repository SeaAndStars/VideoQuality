using Avalonia.Controls;
using Avalonia.Interactivity;
using VideoQuality.App.Services;
using VideoQuality.App.ViewModels;
using VideoQuality.Core.Localization;

namespace VideoQuality.App.Views;

public partial class EncodeView : UserControl
{
    public EncodeView()
    {
        InitializeComponent();
        PageIo.AcceptDrops(this, paths => ViewModel.AddPathsAsync(paths));
    }

    private EncodeViewModel ViewModel => (EncodeViewModel)DataContext!;

    private async void OnAddFiles(object? sender, RoutedEventArgs e) =>
        await ViewModel.AddPathsAsync(await PageIo.PickFilesAsync(this, Loc.T("encode.picker.add"), true));

    private async void OnPickOutput(object? sender, RoutedEventArgs e)
    {
        if (await PageIo.PickFolderAsync(this, Loc.T("picker.outputFolder")) is { } folder)
        {
            ViewModel.OutputDirectory = folder;
        }
    }

    private static readonly (string Name, string[] Patterns) PresetFiles = (Loc.T("picker.type.presets"), ["*.json"]);

    private async void OnImportPresets(object? sender, RoutedEventArgs e) =>
        ViewModel.ImportPresetFiles(await PageIo.PickFilesAsync(this, Loc.T("encode.picker.importPresets"), true, PresetFiles));

    private async void OnExportPreset(object? sender, RoutedEventArgs e)
    {
        string name = ViewModel.SaveName.Trim().Length > 0 ? ViewModel.SaveName.Trim() : ViewModel.Editor.Name;
        if (await PageIo.PickSaveFileAsync(this, Loc.T("encode.picker.exportPresets"), Core.Release.ReleaseName.Sanitize(name) + ".json", "json") is { } file)
        {
            ViewModel.ExportCurrentPreset(file);
        }
    }

    private async void OnExportAllPresets(object? sender, RoutedEventArgs e)
    {
        if (await PageIo.PickSaveFileAsync(this, Loc.T("encode.picker.exportPresets"), "presets.json", "json") is { } file)
        {
            ViewModel.ExportUserPresets(file);
        }
    }
}
