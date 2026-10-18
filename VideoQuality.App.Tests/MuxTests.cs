using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using FluentIcons.Common;
using VideoQuality.App.ViewModels;
using VideoQuality.App.Views;
using VideoQuality.Core.Localization;
using VideoQuality.Core.Mkv;
using VideoQuality.Core.Tools;
using VideoQuality.Tests;

namespace VideoQuality.App.Tests;

public sealed class MuxTests : IDisposable
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures", "fonts");
    private readonly string _work = Directory.CreateTempSubdirectory("vq-app-mux-").FullName;
    private readonly Dictionary<string, string> _identify = new(StringComparer.Ordinal);

    public void Dispose() => Directory.Delete(_work, true);

    private string Touch(string name, string? json = null)
    {
        string path = Path.Combine(_work, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        if (json is not null)
        {
            _identify[path] = json;
        }

        return path;
    }

    private static string Subtitle(string language, string name) =>
        ScriptedMkv.Identify("SSA/ASS subtitles", 0, 0, [(0, "subtitles", "SubStationAlpha", language, name)]);

    private async Task<(MuxViewModel Vm, List<IReadOnlyList<string>> Muxes)> PageAsync(Func<int>? muxExit = null)
    {
        var muxes = new List<IReadOnlyList<string>>();
        SettingsViewModel settings = await Pages.SettingsAsync(tools: false);
        settings.MkvToolNix = ScriptedMkv.Create(request =>
        {
            if (request.Arguments[0] == "-J")
            {
                return _identify.TryGetValue(request.Arguments[1], out string? json)
                    ? ScriptedRunner.Ok(json)
                    : new ProcessResult(2, "Error: not a media file", string.Empty);
            }

            // mkvmerge gets its arguments in a JSON option file (@file), deleted after the run.
            muxes.Add([.. request.Arguments.Select(a => a.StartsWith('@') && File.Exists(a[1..]) ? File.ReadAllText(a[1..]) : a)]);
            int exit = muxExit?.Invoke() ?? 0;
            ScriptedRunner.Emit(request, "#GUI#progress 100%", exit == 1 ? "#GUI#warning odd" : "#GUI#progress 100%");
            return new ProcessResult(exit, string.Empty, exit == 2 ? "write error" : string.Empty);
        });
        return (new MuxViewModel(settings), muxes);
    }

    [AvaloniaFact]
    public async Task Inputs_get_greentea_track_metadata()
    {
        (MuxViewModel vm, _) = await PageAsync();
        string video = Touch("ep01.mkv", ScriptedMkv.Identify("Matroska", 1440, 5,
        [
            (0, "video", "HEVC", "und", ""),
            (1, "audio", "AAC", "und", "Stereo"),
            (2, "audio", "AAC", "eng", "Commentary"),
            (3, "subtitles", "SubStationAlpha", "", ""),
            (4, "buttons", "VobBtn", "", ""),
        ], [(1, "A.ttf", "font/ttf")]));
        string jpsc = Touch("ep01.JPSC.ass", Subtitle("und", ""));
        string tagged = Touch("ep01.JPTC.ass", Subtitle("jpn", "Mine"));
        string plain = Touch("notes.ass", Subtitle("", ""));

        await vm.AddPathsAsync([video, jpsc, tagged, plain, video]);
        Assert.Equal(4, vm.Inputs.Count); // the repeat is ignored
        MuxInputRow first = vm.Inputs[0];
        Assert.True(first.HasAttachments);
        Assert.True(first.HasChapters);
        Assert.Equal("ep01.mkv", first.FileName);
        Assert.Equal([Symbol.Video, Symbol.Speaker2, Symbol.Speaker2, Symbol.ClosedCaption, Symbol.Document], first.Tracks.Select(t => t.Icon));
        Assert.Equal(("ja", string.Empty), (first.Tracks[1].Language, first.Tracks[1].Name)); // untagged audio is Japanese, unnamed
        Assert.Equal(("eng", string.Empty), (first.Tracks[2].Language, first.Tracks[2].Name));
        Assert.True(first.Tracks[3].Default); // the first subtitle
        Assert.Equal(("zh", "JPSC", false), (vm.Inputs[1].Tracks[0].Language, vm.Inputs[1].Tracks[0].Name, vm.Inputs[1].Tracks[0].Default));
        Assert.Equal(("jpn", "Mine"), (vm.Inputs[2].Tracks[0].Language, vm.Inputs[2].Tracks[0].Name)); // already tagged
        Assert.Equal((string.Empty, string.Empty), (vm.Inputs[3].Tracks[0].Language, vm.Inputs[3].Tracks[0].Name)); // no guess
        Assert.Equal(Path.Combine(_work, "ep01.mux.mkv"), vm.Output);
        Assert.Contains("4", vm.Status, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Generic_naming_keeps_audio_and_a_script_alone_comes_first()
    {
        (MuxViewModel vm, _) = await PageAsync();
        vm.Settings.TrackNaming = TrackNamingProfile.Generic;
        string script = Touch("ep01.chs.ass", Subtitle("", ""));
        await vm.AddPathsAsync([script]);
        Assert.True(vm.Inputs[0].Tracks[0].Default);
        Assert.Equal(("zh-Hans", "简体中文"), (vm.Inputs[0].Tracks[0].Language, vm.Inputs[0].Tracks[0].Name));
        Assert.Equal(string.Empty, vm.Output); // no video yet

        string video = Touch("ep01.mp4", ScriptedMkv.Identify("MP4/QuickTime", 60, 0, [(0, "video", "AVC", "", ""), (1, "audio", "AAC", "und", "Main")]));
        await vm.AddPathsAsync([video]);
        Assert.Equal(("und", "Main"), (vm.Inputs[1].Tracks[1].Language, vm.Inputs[1].Tracks[1].Name));
        Assert.False(vm.Inputs[1].HasAttachments);
        Assert.False(vm.Inputs[1].HasChapters);
        Assert.Equal(Path.Combine(_work, "ep01.mkv"), vm.Output);
    }

    [AvaloniaFact]
    public async Task Fonts_folders_and_chapters_are_sorted_out()
    {
        (MuxViewModel vm, _) = await PageAsync();
        Assert.Equal(Loc.T("mux.attachments.none"), vm.AttachmentSummary);
        string fonts = Directory.CreateDirectory(Path.Combine(_work, "fonts", "sub")).FullName;
        File.Copy(Path.Combine(Fixtures, "DejaVuSans.ttf"), Path.Combine(fonts, "A.ttf"));
        File.WriteAllText(Path.Combine(fonts, "readme.txt"), "x");
        string single = Touch("B.OTF");
        string chapters = Touch("chapters.xml");

        await vm.AddPathsAsync([Path.Combine(_work, "fonts"), single, single, chapters, Path.Combine(_work, "missing.mkv"), null]);
        Assert.Equal(2, vm.Attachments.Count);
        Assert.Equal(chapters, vm.Chapters);
        Assert.Contains("2", vm.AttachmentSummary, StringComparison.Ordinal);
        Assert.Empty(vm.Inputs);

        vm.RemoveAttachmentCommand.Execute(vm.Attachments[0]);
        Assert.Single(vm.Attachments);

        await vm.AddPathsAsync([Touch("unknown.bin")]);
        Assert.Contains("not a media file", vm.Status, StringComparison.Ordinal);

        vm.ClearAllCommand.Execute(null);
        Assert.Empty(vm.Attachments);
        Assert.Equal(string.Empty, vm.Chapters);
        Assert.False(vm.HasInputs);
    }

    [AvaloniaFact]
    public async Task Subset_output_arrives_with_or_without_a_video()
    {
        (MuxViewModel vm, _) = await PageAsync();
        string font = Touch("ABCDEFGH.ttf");
        await vm.AddSubsetOutputAsync([Touch("ep01.JPSC.ass", Subtitle("", ""))], [font]);
        Assert.Equal(Loc.T("mux.status.needVideo"), vm.Status);
        Assert.Single(vm.Attachments);

        await vm.AddPathsAsync([Touch("ep01.mkv", ScriptedMkv.Identify("Matroska", 60, 0, [(0, "video", "HEVC", "", "")]))]);
        string status = vm.Status;
        await vm.AddSubsetOutputAsync([], []);
        Assert.Equal(status, vm.Status);
    }

    [AvaloniaFact]
    public async Task Muxing_reports_success_warnings_and_failures()
    {
        int exit = 0;
        (MuxViewModel vm, List<IReadOnlyList<string>> muxes) = await PageAsync(() => exit);
        Assert.False(vm.MuxCommand.CanExecute(null));
        string video = Touch("ep01.mkv", ScriptedMkv.Identify("Matroska", 60, 0, [(0, "video", "HEVC", "", ""), (1, "audio", "AAC", "", "")]));
        await vm.AddPathsAsync([video, Touch("f.ttf")]);
        _identify[vm.Output] = ScriptedMkv.Identify("Matroska", 60, 0, [(0, "video", "HEVC", "", "")], [(1, "f.ttf", "font/ttf")]);
        var toasts = new List<ToastRequest>();
        vm.ToastRequested += (_, t) => toasts.Add(t);

        vm.Inputs[0].Tracks[1].Include = false;
        vm.Title = "Episode 1";
        vm.Chapters = Touch("c.xml");
        await vm.MuxCommand.ExecuteAsync(null);
        Assert.Equal(ToastKind.Success, toasts[^1].Kind);
        Assert.EndsWith(".", vm.Status, StringComparison.Ordinal);
        Assert.Equal(1, vm.Progress);
        Assert.Contains("Episode 1", string.Join(' ', muxes[^1]), StringComparison.Ordinal);

        exit = 1;
        vm.Title = string.Empty;
        vm.Chapters = string.Empty;
        await vm.MuxCommand.ExecuteAsync(null);
        Assert.Contains("odd", vm.Status, StringComparison.Ordinal);

        exit = 2;
        await vm.MuxCommand.ExecuteAsync(null);
        Assert.Contains("write error", vm.Status, StringComparison.Ordinal);
        Assert.Equal(ToastKind.Error, toasts[^1].Kind);

        exit = 0;
        _identify.Remove(vm.Output); // the written file cannot be read back
        await vm.MuxCommand.ExecuteAsync(null);
        Assert.Equal(ToastKind.Error, toasts[^1].Kind);

        vm.Output = string.Empty;
        await vm.MuxCommand.ExecuteAsync(null);
        Assert.Equal(Loc.T("mux.status.needOutput"), vm.Status);

        vm.RemoveInputCommand.Execute(vm.Inputs[0]);
        Assert.False(vm.MuxCommand.CanExecute(null));
        vm.Settings.MkvToolNix = null;
        await vm.AddPathsAsync([video]);
        Assert.Equal(Loc.T("common.mkvtoolnixRequired"), vm.Status);
    }

    [AvaloniaFact]
    public async Task Real_mux_from_the_page()
    {
        SettingsViewModel settings = await Pages.SettingsAsync();
        var vm = new MuxViewModel(settings);
        var view = new MuxView();
        Window window = Ui.Show(view, vm);
        string clip = Path.Combine(_work, "clip.mkv");
        File.Copy(await TestMedia.ClipAsync(), clip);
        string script = TestMedia.Script(_work, "clip.JPSC.ass");
        string fonts = Directory.CreateDirectory(Path.Combine(_work, "fonts")).FullName;
        File.Copy(Path.Combine(Fixtures, "DejaVuSans.ttf"), Path.Combine(fonts, "DejaVuSans.ttf"));
        string output = Path.Combine(_work, "release.mkv");
        using var scope = new DialogScope(new ScriptedDialogs().Answer(new[] { clip }, fonts, null, null, output, new[] { Touch("c2.xml") }, null));

        Ui.Invoke(view, "OnAddFiles");
        await Ui.UntilAsync(() => vm.HasInputs);
        Ui.Invoke(view, "OnAddFonts");
        Ui.Invoke(view, "OnAddFonts"); // cancelled
        await Ui.UntilAsync(() => vm.Attachments.Count == 1);
        Ui.Invoke(view, "OnPickOutput"); // cancelled: suggests the current name
        Ui.Invoke(view, "OnPickOutput");
        Ui.Invoke(view, "OnPickChapters");
        Ui.Invoke(view, "OnPickChapters"); // cancelled
        Assert.Equal(output, vm.Output);
        Assert.EndsWith("c2.xml", vm.Chapters, StringComparison.Ordinal);
        Assert.Contains(scope.Dialogs.Titles, t => t.EndsWith("|clip.mux.mkv", StringComparison.Ordinal));
        vm.Chapters = string.Empty;

        Ui.Drop(window, view, script);
        await Ui.UntilAsync(() => vm.Inputs.Count == 2);
        await vm.MuxCommand.ExecuteAsync(null);
        MkvFileInfo written = await settings.MkvToolNix!.IdentifyAsync(output);
        Assert.Contains(written.Tracks, t => t.Type == MkvTrackType.Subtitles && t.Name == "JPSC");
        Assert.Single(written.Attachments);

        vm.Output = string.Empty;
        Ui.Invoke(view, "OnPickOutput"); // a default name
        Assert.Contains(scope.Dialogs.Titles, t => t.EndsWith("|release.mkv", StringComparison.Ordinal));
        window.Close();
    }
}
