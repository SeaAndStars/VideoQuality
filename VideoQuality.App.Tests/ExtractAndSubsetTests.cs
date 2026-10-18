using System.Buffers.Binary;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using FluentIcons.Common;
using VideoQuality.App.Services;
using VideoQuality.App.ViewModels;
using VideoQuality.App.Views;
using VideoQuality.Core.Fonts;
using VideoQuality.Core.Localization;
using VideoQuality.Core.Mkv;
using VideoQuality.Core.Tools;
using VideoQuality.Tests;

namespace VideoQuality.App.Tests;

/// <summary>Settings kept in memory, so tests never see each other's choices.</summary>
public sealed class MemorySettings : ISettingsStore
{
    private string _json;

    public MemorySettings(string json = "{}") => _json = json;

    public int Saves { get; private set; }

    public UserSettings Load() => System.Text.Json.JsonSerializer.Deserialize<UserSettings>(_json)!;

    public void Save(UserSettings settings)
    {
        _json = System.Text.Json.JsonSerializer.Serialize(settings);
        Saves++;
    }
}

internal static class Pages
{
    public static AppServices Isolated(IProcessRunner? processes = null) =>
        new() { Processes = processes ?? SystemProcessRunner.Instance, Settings = new MemorySettings() };

    public static readonly ScriptedRunner NothingRuns = new(_ => throw new ProcessStartException("x", new System.ComponentModel.Win32Exception()));

    /// <summary>Settings whose tool search has finished (real tools, or none).</summary>
    public static async Task<SettingsViewModel> SettingsAsync(bool tools = true)
    {
        var settings = new SettingsViewModel(new MainViewModel(Pages.Isolated()), tools ? Pages.Isolated() : Pages.Isolated(NothingRuns));
        await Ui.UntilAsync(() => settings.MkvToolNixStatus != Loc.T("settings.mkv.looking") && settings.EncoderStatus != Loc.T("settings.encoder.looking"));
        if (tools)
        {
            settings.EncoderFolder = Path.GetDirectoryName(TestMedia.RequireEncoder())!;
            await settings.LocateToolsAsync();
        }

        return settings;
    }
}

public sealed class ExtractTests : IDisposable
{
    private static readonly (int, string, string, string, string)[] Tracks =
    [
        (0, "video", "AVC/H.264", "und", ""),
        (1, "audio", "AAC", "jpn", ""),
        (2, "subtitles", "SubStationAlpha", "chi", "JPSC"),
        (3, "buttons", "VobBtn", "", ""),
    ];

    private static readonly (int, string, string)[] Attachments =
    [
        (1, "A.ttf", "font/ttf"),
        (2, "B.otf", "application/octet-stream"),
        (3, "notes.txt", "text/plain"),
    ];

    private readonly string _work = Directory.CreateTempSubdirectory("vq-app-extract-").FullName;

    public void Dispose() => Directory.Delete(_work, true);

    private string Media(string name)
    {
        string path = Path.Combine(_work, name);
        File.WriteAllText(path, "media");
        return path;
    }

    [AvaloniaFact]
    public async Task Without_mkvtoolnix_nothing_opens()
    {
        var vm = new ExtractViewModel(await Pages.SettingsAsync(tools: false));
        await vm.OpenAsync(Media("a.mkv"));
        Assert.Equal(Loc.T("common.mkvtoolnixRequired"), vm.Status);
        Assert.False(vm.HasSource);
        Assert.False(vm.HasChapters);
        Assert.Equal(string.Empty, vm.Summary);
        Assert.False(vm.ExtractCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task Tracks_and_attachments_are_listed_selected_and_extracted()
    {
        var calls = new List<IReadOnlyList<string>>();
        int extractExit = 1;
        MkvToolNix tools = ScriptedMkv.Create(request =>
        {
            calls.Add(request.Arguments);
            if (request.Arguments[0] == "-J")
            {
                return ScriptedRunner.Ok(ScriptedMkv.Identify("Matroska", 90, 3, Tracks, Attachments));
            }

            ScriptedRunner.Emit(request, "#GUI#progress 50%", "#GUI#warning odd timestamps");
            return new ProcessResult(extractExit, string.Empty, extractExit == 2 ? "disk full" : string.Empty);
        });
        SettingsViewModel settings = await Pages.SettingsAsync(tools: false);
        var vm = new ExtractViewModel(settings);
        settings.MkvToolNix = tools;
        var toasts = new List<ToastRequest>();
        vm.ToastRequested += (_, t) => toasts.Add(t);

        string source = Media("ep01.mkv");
        await vm.OpenAsync(source);
        Assert.True(vm.HasChapters, vm.Status);
        Assert.True(vm.ExtractChapters);
        Assert.Contains("3", vm.Summary, StringComparison.Ordinal);
        Assert.Equal([Symbol.Video, Symbol.Speaker2, Symbol.ClosedCaption, Symbol.Document, Symbol.TextFont, Symbol.TextFont, Symbol.Attach], vm.Items.Select(i => i.Icon));
        Assert.Equal("ep01.track1.jpn.aac", vm.Items[1].OutputName);
        Assert.Equal("ep01.track0.h264", vm.Items[0].OutputName);
        Assert.Equal("JPSC", vm.Items[2].Title);
        Assert.Contains(Loc.T("track.flag.forced"), vm.Items[2].Details, StringComparison.Ordinal);
        Assert.Contains(Loc.T("track.flag.default"), vm.Items[0].Details, StringComparison.Ordinal);
        Assert.Equal(Path.Combine(_work, "ep01_extracted"), vm.OutputDirectory);

        vm.SelectKindCommand.Execute("all");
        Assert.All(vm.Items, i => Assert.True(i.IsSelected));
        vm.SelectKindCommand.Execute("subtitles");
        Assert.Equal([2], vm.Items.Where(i => i.IsSelected).Select(i => i.Id));
        vm.SelectKindCommand.Execute("other"); // unknown: unchanged
        Assert.Single(vm.Items, i => i.IsSelected);
        vm.SelectKindCommand.Execute("fonts");
        Assert.Equal(2, vm.Items.Count(i => i.IsSelected));

        await vm.ExtractCommand.ExecuteAsync(null);
        Assert.Equal(1, vm.Progress);
        Assert.Contains(Loc.T("extract.status.warnings", 1), vm.Status, StringComparison.Ordinal);
        Assert.Equal(ToastKind.Success, toasts[^1].Kind);
        Assert.Contains("attachments", calls[^1]);
        Assert.Contains("chapters", calls[^1]);

        extractExit = 0;
        vm.SelectKindCommand.Execute("subtitles");
        vm.ExtractChapters = false;
        await vm.ExtractCommand.ExecuteAsync(null);
        Assert.EndsWith(".", vm.Status, StringComparison.Ordinal);

        extractExit = 2;
        await vm.ExtractCommand.ExecuteAsync(null);
        Assert.Contains("disk full", vm.Status, StringComparison.Ordinal);
        Assert.Equal(ToastKind.Error, toasts[^1].Kind);

        vm.SelectKindCommand.Execute("none");
        await vm.ExtractCommand.ExecuteAsync(null);
        Assert.Equal(Loc.T("extract.status.nothingSelected"), vm.Status);

        // An output folder that cannot be created.
        vm.SelectKindCommand.Execute("all");
        vm.OutputDirectory = Path.Combine(source, "out");
        await vm.ExtractCommand.ExecuteAsync(null);
        Assert.Equal(ToastKind.Error, toasts[^1].Kind);
    }

    [AvaloniaFact]
    public async Task Other_containers_are_remuxed_first_and_failures_explained()
    {
        int muxExit = 0;
        int identifyExit = 0;
        MkvToolNix tools = ScriptedMkv.Create(request =>
        {
            if (request.Arguments[0] == "-J")
            {
                return identifyExit == 0
                    ? ScriptedRunner.Ok(ScriptedMkv.Identify("MP4/QuickTime", 10, 0, [(0, "video", "AVC/H.264", "", "")]))
                    : new ProcessResult(2, "Error: unsupported file", string.Empty);
            }

            if (request.FileName.EndsWith("mkvmerge", StringComparison.Ordinal))
            {
                return new ProcessResult(muxExit, muxExit == 0 ? "#GUI#progress 100%" : string.Empty, muxExit == 0 ? string.Empty : "cannot read");
            }

            return ScriptedRunner.Ok();
        });
        SettingsViewModel settings = await Pages.SettingsAsync(tools: false);
        settings.MkvToolNix = tools;
        var vm = new ExtractViewModel(settings); // nobody listens for toasts

        await vm.OpenAsync(Media("clip.mp4"));
        Assert.False(vm.HasChapters);
        Assert.False(vm.ExtractChapters);
        vm.SelectKindCommand.Execute("all");
        await vm.ExtractCommand.ExecuteAsync(null);
        Assert.Equal(1, vm.Progress);

        muxExit = 2;
        await vm.ExtractCommand.ExecuteAsync(null);
        Assert.Contains("cannot read", vm.Status, StringComparison.Ordinal);

        identifyExit = 2;
        await vm.OpenAsync(Media("bad.mp4"));
        Assert.Contains("unsupported file", vm.Status, StringComparison.Ordinal);
        Assert.False(vm.IsBusy);
    }

    [AvaloniaFact]
    public async Task Real_files_extract_from_the_page()
    {
        SettingsViewModel settings = await Pages.SettingsAsync();
        var vm = new ExtractViewModel(settings);
        var view = new ExtractView();
        Window window = Ui.Show(view, vm);
        string rich = Path.Combine(_work, "rich.mkv");
        File.Copy(await TestMedia.RichAsync(), rich);
        string output = Path.Combine(_work, "picked");
        using var scope = new DialogScope(new ScriptedDialogs().Answer(new[] { rich }, null, output, null));

        Ui.Invoke(view, "OnOpenOutput"); // nothing open yet: no folder to reveal
        Ui.Invoke(view, "OnOpen");
        await Ui.UntilAsync(() => vm.Info is not null && !vm.IsBusy);
        Ui.Invoke(view, "OnOpen");
        Ui.Invoke(view, "OnPickOutput");
        Ui.Invoke(view, "OnPickOutput");
        Assert.Equal(output, vm.OutputDirectory);
        Ui.Invoke(view, "OnOpenOutput");
        Assert.Equal([output], scope.Dialogs.Revealed);

        vm.SelectKindCommand.Execute("all");
        await vm.ExtractCommand.ExecuteAsync(null);
        Assert.True(File.Exists(Path.Combine(output, "attachments", "DejaVuSans.ttf")), vm.Status);
        Assert.Contains(Directory.GetFiles(output), f => f.EndsWith(".ass", StringComparison.Ordinal));

        vm.OutputDirectory = string.Empty;
        Ui.Drop(window, view, Path.Combine(_work, "missing.mkv")); // not a file: ignored
        Ui.Drop(window, view, rich);
        await Ui.UntilAsync(() => vm.OutputDirectory.Length > 0 && !vm.IsBusy);
        window.Close();
    }
}

public sealed class FontSubsetPageTests : IDisposable
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures", "fonts");
    private readonly string _work = Directory.CreateTempSubdirectory("vq-app-subset-").FullName;

    public void Dispose() => Directory.Delete(_work, true);

    /// <summary>DejaVu Sans and DejaVu Sans Bold in one collection (the bold face is #1).</summary>
    private static byte[] Collection()
    {
        byte[] regular = File.ReadAllBytes(Path.Combine(Fixtures, "DejaVuSans.ttf"));
        byte[] bold = File.ReadAllBytes(Path.Combine(Fixtures, "DejaVuSans-Bold.ttf"));
        const int Header = 20;
        byte[] file = [.. "ttcf"u8, 0, 1, 0, 0, 0, 0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, .. regular, .. bold];
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(12), Header);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(16), (uint)(Header + regular.Length));
        Shift(file, Header, Header);
        Shift(file, Header + regular.Length, Header + regular.Length);
        return file;

        static void Shift(byte[] file, int font, int by)
        {
            int tables = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(font + 4));
            for (int i = 0; i < tables; i++)
            {
                Span<byte> offset = file.AsSpan(font + 12 + (16 * i) + 8, 4);
                BinaryPrimitives.WriteUInt32BigEndian(offset, BinaryPrimitives.ReadUInt32BigEndian(offset) + (uint)by);
            }
        }
    }

    private (string Scripts, string Fonts) Folders()
    {
        string scripts = Directory.CreateDirectory(Path.Combine(_work, "subs")).FullName;
        string fonts = Directory.CreateDirectory(Path.Combine(scripts, "Fonts")).FullName;
        File.WriteAllBytes(Path.Combine(fonts, "DejaVu.ttc"), Collection());
        File.Copy(Path.Combine(Fixtures, "VQTestCff.otf"), Path.Combine(fonts, "VQTestCff.otf"));
        File.WriteAllText(Path.Combine(fonts, "broken.ttf"), "not a font");
        File.WriteAllText(Path.Combine(scripts, "ep01.ass"), """
            [V4+ Styles]
            Format: Name, Fontname, Bold, Italic
            Style: Default,DejaVu Sans,0,0
            Style: Unused,Never Drawn,0,0

            [Events]
            Format: Style, Text
            Dialogue: Default,一二三四五六七八九{\b1}Bold{\b0\fnVQ Test CFF}é{\b1\i1}x{\b1\i0}y{\b0\i1}z{\i0\fnNope}n
            """);
        File.WriteAllText(Path.Combine(scripts, "ep01.ssa"), "[Events]\nFormat: Style, Text\nDialogue: x,{\\fnVQ Test CFF}ü");
        File.WriteAllText(Path.Combine(scripts, "notes.txt"), "ignored");
        return (scripts, fonts);
    }

    [AvaloniaFact]
    public async Task Scripts_are_collected_subset_and_reported()
    {
        (string scripts, string fonts) = Folders();
        var vm = new FontSubsetViewModel();
        Assert.False(vm.RunCommand.CanExecute(null));
        vm.AddPaths([scripts, Path.Combine(scripts, "ep01.ass"), Path.Combine(scripts, "notes.txt"), null]);
        Assert.Equal(2, vm.Scripts.Count); // the folder's two scripts, the repeat ignored
        Assert.True(vm.HasScripts);
        Assert.Equal(fonts, vm.FontDirectory);
        Assert.Equal(Path.Combine(scripts, "output"), vm.OutputDirectory);

        var toasts = new List<ToastRequest>();
        vm.ToastRequested += (_, t) => toasts.Add(t);
        await vm.RunCommand.ExecuteAsync(null);

        Assert.Equal(ToastKind.Warning, toasts[^1].Kind); // "Nope" is missing
        Assert.Contains(vm.Fonts, f => f.SourceFile.EndsWith(" #1", StringComparison.Ordinal));
        Assert.Contains(vm.Notes, n => n.Kind == ToastKind.Error);
        Assert.Contains(vm.Notes, n => n.Text.Contains(Loc.T("subset.style.boldItalic"), StringComparison.Ordinal));
        Assert.Contains(vm.Notes, n => n.Text.Contains(Loc.T("subset.style.bold"), StringComparison.Ordinal));
        Assert.Contains(vm.Notes, n => n.Text.Contains(Loc.T("subset.style.italic"), StringComparison.Ordinal));
        Assert.Contains(vm.Notes, n => n.Text.Contains(" …", StringComparison.Ordinal));
        Assert.Contains(vm.Notes, n => n.Kind == ToastKind.Info);
        Assert.Contains(vm.Notes, n => n.Text.Contains("broken.ttf", StringComparison.Ordinal));
        Assert.True(vm.HasResult);

        (IReadOnlyList<string> Scripts, IReadOnlyList<string> Fonts)? sent = null;
        vm.SendToMuxRequested += (_, output) => sent = output;
        vm.SendToMuxCommand.Execute(null);
        Assert.Equal(2, sent!.Value.Scripts.Count);

        // Without the missing font the run succeeds; without listeners nothing is announced.
        vm.RemoveScriptCommand.Execute(vm.Scripts.First(s => s.EndsWith(".ass", StringComparison.Ordinal)));
        await vm.RunCommand.ExecuteAsync(null);
        Assert.Equal(ToastKind.Success, toasts[^1].Kind);
        Assert.DoesNotContain(vm.Notes, n => n.Kind == ToastKind.Error);
        var quiet = new FontSubsetViewModel();
        quiet.AddPaths([Path.Combine(scripts, "ep01.ssa"), Path.Combine(fonts, "VQTestCff.otf")]);
        await quiet.RunCommand.ExecuteAsync(null);
        quiet.SendToMuxCommand.Execute(null);

        vm.ClearCommand.Execute(null);
        Assert.False(vm.HasScripts);
        Assert.False(vm.HasResult);
        Assert.Equal(string.Empty, vm.FontDirectory);
    }

    [AvaloniaFact]
    public async Task Failures_are_reported_and_a_font_file_picks_its_folder()
    {
        (string scripts, string fonts) = Folders();
        var vm = new FontSubsetViewModel();
        vm.AddPaths([Path.Combine(fonts, "VQTestCff.otf")]);
        Assert.Equal(fonts, vm.FontDirectory);
        vm.AddPaths([Path.Combine(scripts, "ep01.ssa")]);
        Assert.Equal(fonts, vm.FontDirectory); // kept

        vm.OutputDirectory = scripts; // the scripts' own folder is refused
        var toasts = new List<ToastRequest>();
        vm.ToastRequested += (_, t) => toasts.Add(t);
        await vm.RunCommand.ExecuteAsync(null);
        Assert.Equal(ToastKind.Error, Assert.Single(toasts).Kind);
        Assert.False(vm.IsBusy);

        // A script folder without a fonts folder uses itself.
        string bare = Directory.CreateDirectory(Path.Combine(_work, "bare")).FullName;
        File.WriteAllText(Path.Combine(bare, "a.ass"), string.Empty);
        var other = new FontSubsetViewModel();
        other.AddPaths([Path.Combine(bare, "a.ass")]);
        Assert.Equal(bare, other.FontDirectory);
    }

    [Theory]
    [InlineData(512, "0.5 KB")]
    [InlineData(3 * 1024 * 1024, "3.0 MB")]
    public void Sizes_read_in_kilobytes_or_megabytes(long bytes, string expected) =>
        Assert.Equal(expected, FontSubsetViewModel.Size(bytes));

    [AvaloniaFact]
    public async Task Font_subset_page_buttons()
    {
        (string scripts, string fonts) = Folders();
        var vm = new FontSubsetViewModel();
        var view = new FontSubsetView();
        Window window = Ui.Show(view, vm);
        using var scope = new DialogScope(new ScriptedDialogs().Answer(
            new[] { Path.Combine(scripts, "ep01.ssa") }, fonts, null, "/out", null));

        Ui.Invoke(view, "OnOpenOutput"); // no output folder yet
        Ui.Invoke(view, "OnAddScripts");
        Ui.Invoke(view, "OnPickFontFolder");
        Ui.Invoke(view, "OnPickFontFolder");
        Ui.Invoke(view, "OnPickOutputFolder");
        Ui.Invoke(view, "OnPickOutputFolder");
        Ui.Invoke(view, "OnOpenOutput");
        await Ui.SettleAsync();
        Assert.Single(vm.Scripts);
        Assert.Equal(("/out", fonts), (vm.OutputDirectory, vm.FontDirectory));
        Assert.Equal(["/out"], scope.Dialogs.Revealed);

        Ui.Drop(window, view, Path.Combine(scripts, "ep01.ass"));
        Assert.Equal(2, vm.Scripts.Count);
        window.Close();
    }
}
