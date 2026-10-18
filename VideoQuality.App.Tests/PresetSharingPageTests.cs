using System.Net;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using VideoQuality.App.Services;
using VideoQuality.App.ViewModels;
using VideoQuality.App.Views;
using VideoQuality.Core.Encode;
using VideoQuality.Core.Localization;
using VideoQuality.Tests;

namespace VideoQuality.App.Tests;

public sealed class PresetSharingPageTests : IDisposable
{
    private readonly string _work = Directory.CreateTempSubdirectory("vq-app-presets-").FullName;

    public void Dispose() => Directory.Delete(_work, true);

    private (EncodeViewModel Vm, List<ToastRequest> Toasts) Page(Func<HttpRequestMessage, HttpResponseMessage>? http = null)
    {
        var services = new AppServices
        {
            Processes = Pages.NothingRuns,
            Settings = new MemorySettings(),
            Http = new HttpClient(new ScriptedHttp(http ?? (_ => ScriptedHttp.Json("[]")))),
        };
        var settings = new SettingsViewModel(new MainViewModel(services), services);
        var vm = new EncodeViewModel(settings, new EncodeQueue(Pages.NothingRuns, () => "ffmpeg"), new PresetStore(Path.Combine(_work, "store-" + Guid.NewGuid().ToString("N")[..6])));
        var toasts = new List<ToastRequest>();
        vm.ToastRequested += (_, t) => toasts.Add(t);
        return (vm, toasts);
    }

    [AvaloniaFact]
    public void Preset_files_are_imported_and_exported()
    {
        (EncodeViewModel vm, List<ToastRequest> toasts) = Page();
        string good = Path.Combine(_work, "good.json");
        File.WriteAllText(good, PresetStore.WriteBundle([new EncodePreset { Name = "Shared A" }, new EncodePreset { Name = "Shared B" }]));
        string bad = Path.Combine(_work, "bad.json");
        File.WriteAllText(bad, "not json");

        vm.ImportPresetFiles([good, bad, Path.Combine(_work, "missing.json")]);
        Assert.Equal(2, toasts.Count(t => t.Kind == ToastKind.Error));
        Assert.Equal(ToastKind.Success, toasts[^1].Kind);
        Assert.Equal("Shared B", vm.SelectedPreset!.Name);
        vm.ImportPresetFiles([bad]); // nothing imported: no success toast
        Assert.Equal(ToastKind.Error, toasts[^1].Kind);

        string one = Path.Combine(_work, "one.json");
        vm.SaveName = "Renamed";
        vm.ExportCurrentPreset(one);
        Assert.Equal("Renamed", PresetStore.Read(File.ReadAllText(one)).Name);
        vm.SaveName = " ";
        vm.ExportCurrentPreset(one);
        Assert.Equal("Shared B", PresetStore.Read(File.ReadAllText(one)).Name);

        string all = Path.Combine(_work, "all.json");
        vm.ExportUserPresets(all);
        Assert.Equal(["Shared A", "Shared B"], PresetStore.Parse(File.ReadAllText(all)).Select(p => p.Name));
        vm.ExportUserPresets(Path.Combine(_work, "no", "such", "folder", "x.json"));
        Assert.Equal(ToastKind.Error, toasts[^1].Kind);

        (EncodeViewModel empty, List<ToastRequest> emptyToasts) = Page();
        empty.ExportUserPresets(all);
        Assert.Equal(Loc.T("encode.presets.nothingToExport"), emptyToasts[^1].Message);
    }

    [AvaloniaFact]
    public async Task Online_presets_are_listed_and_downloaded()
    {
        string listing = """[{"name":"a.json","type":"file","download_url":"https://raw/a.json"}]""";
        (EncodeViewModel vm, List<ToastRequest> toasts) = Page(request => request.RequestUri!.Host == "api.github.com"
            ? ScriptedHttp.Json(listing)
            : ScriptedHttp.Json(PresetStore.WriteBundle([new EncodePreset { Name = "Online 1", Description = "d" }, new EncodePreset { Name = "Online 2" }])));
        Assert.Equal(PresetSource.Default, vm.PresetSource);
        Assert.False(vm.HasOnlinePresets);

        Task load = vm.LoadOnlineCommand.ExecuteAsync(null);
        await load;
        Assert.True(vm.HasOnlinePresets);
        Assert.Equal(Loc.T("encode.presets.found", 2), vm.OnlineStatus);
        Assert.True(vm.LoadOnlineCommand.CanExecute(null));

        vm.DownloadOnlineCommand.Execute(vm.OnlinePresets[0]);
        Assert.Equal("Online 1", vm.SelectedPreset!.Name);
        vm.DownloadAllOnlineCommand.Execute(null);
        Assert.Contains(vm.Presets, p => p.Name == "Online 1 (2)");
        Assert.Contains(vm.Presets, p => p.Name == "Online 2");

        // The source is remembered unless it is the default.
        vm.PresetSource = "me/repo@dev";
        Assert.Equal("me/repo@dev", vm.Settings.Compare.PersistedSettings.PresetSource);
        vm.PresetSource = PresetSource.Default;
        Assert.Null(vm.Settings.Compare.PersistedSettings.PresetSource);
        vm.PresetSource = " ";
        Assert.Null(vm.Settings.Compare.PersistedSettings.PresetSource);
        await vm.LoadOnlineCommand.ExecuteAsync(null); // blank: the default source
        Assert.True(vm.HasOnlinePresets);
    }

    [AvaloniaFact]
    public async Task Bad_sources_empty_folders_and_errors_are_reported()
    {
        (EncodeViewModel vm, _) = Page(_ => ScriptedHttp.Json("{}", HttpStatusCode.NotFound));
        vm.PresetSource = "not a source";
        await vm.LoadOnlineCommand.ExecuteAsync(null);
        Assert.Contains("not a source", vm.OnlineStatus, StringComparison.Ordinal);
        vm.PresetSource = "me/repo";
        await vm.LoadOnlineCommand.ExecuteAsync(null);
        Assert.Contains("Not found", vm.OnlineStatus, StringComparison.Ordinal);
        Assert.False(vm.HasOnlinePresets);

        (EncodeViewModel empty, _) = Page();
        empty.Settings.Compare.UpdateSettings(s => s.PresetSource = "me/saved");
        var reopened = new EncodeViewModel(empty.Settings, empty.Queue, empty.Store);
        Assert.Equal("me/saved", reopened.PresetSource); // a saved source comes back
        await empty.LoadOnlineCommand.ExecuteAsync(null);
        Assert.Equal(Loc.T("encode.presets.none"), empty.OnlineStatus);
        empty.DownloadAllOnlineCommand.Execute(null); // nothing to download
    }

    [AvaloniaFact]
    public void Page_buttons_pick_files()
    {
        (EncodeViewModel vm, _) = Page();
        var view = new EncodeView();
        Window window = Ui.Show(view, vm);
        string import = Path.Combine(_work, "in.json");
        File.WriteAllText(import, PresetStore.Write(new EncodePreset { Name = "Picked" }));
        string one = Path.Combine(_work, "out.json");
        string all = Path.Combine(_work, "all.json");
        using var scope = new DialogScope(new ScriptedDialogs().Answer(new[] { import }, one, null, null, all, null));

        Ui.Invoke(view, "OnImportPresets");
        Ui.Invoke(view, "OnExportPreset");
        Ui.Invoke(view, "OnExportPreset"); // cancelled
        vm.SaveName = string.Empty;
        Ui.Invoke(view, "OnExportPreset"); // no name typed: the preset's own (cancelled)
        Ui.Invoke(view, "OnExportAllPresets");
        Ui.Invoke(view, "OnExportAllPresets"); // cancelled
        Assert.True(File.Exists(one) && File.Exists(all));
        Assert.Contains(scope.Dialogs.Titles, t => t.EndsWith("|Picked.json", StringComparison.Ordinal));
        Assert.Equal(3, scope.Dialogs.Titles.Count(t => t.EndsWith("|Picked.json", StringComparison.Ordinal)));
        window.Close();
    }
}
