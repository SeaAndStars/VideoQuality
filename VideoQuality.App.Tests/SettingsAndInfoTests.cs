using Avalonia.Headless.XUnit;
using VideoQuality.App.Services;
using VideoQuality.App.ViewModels;
using VideoQuality.App.Views;
using VideoQuality.Core.Localization;
using VideoQuality.Core.Media;
using VideoQuality.Core.Mkv;
using VideoQuality.Core.Release;
using VideoQuality.Core.Tools;
using VideoQuality.Tests;

namespace VideoQuality.App.Tests;

public class SettingsTests
{
    private static readonly ScriptedRunner NothingRuns = new(_ => throw new ProcessStartException("x", new System.ComponentModel.Win32Exception()));

    [AvaloniaFact]
    public async Task Tools_are_located_from_the_chosen_folders()
    {
        var restarts = new List<IReadOnlyList<string>>();
        var services = new AppServices { Restart = restarts.Add, Settings = new MemorySettings() };
        var settings = new SettingsViewModel(new MainViewModel(Pages.Isolated()), services);
        await Ui.UntilAsync(() => settings.HasMkvToolNix && settings.HasEncoder);
        Assert.Contains("mkvmerge", settings.MkvToolNixStatus, StringComparison.Ordinal);

        settings.EncoderFolder = Path.GetDirectoryName(TestMedia.RequireEncoder())!;
        settings.MkvToolNixFolder = " ";
        await settings.LocateToolsAsync();
        Assert.Equal(TestMedia.RequireEncoder(), settings.Encoder!.Path);
        Assert.Contains("libx265", settings.EncoderStatus, StringComparison.Ordinal);

        settings.EncoderFolder = string.Empty;
        settings.MkvToolNixFolder = "/opt/mkvtoolnix";
        Assert.Equal("/opt/mkvtoolnix", settings.Compare.PersistedSettings.MkvToolNixFolder);
        Assert.Null(settings.Compare.PersistedSettings.EncoderFolder);
        settings.EncoderFolder = "/opt/ffmpeg";
        var reopened = new SettingsViewModel(settings.Compare, Pages.Isolated(NothingRuns)); // saved folders come back
        Assert.Equal(("/opt/mkvtoolnix", "/opt/ffmpeg"), (reopened.MkvToolNixFolder, reopened.EncoderFolder));
        settings.MkvToolNixFolder = string.Empty;
        settings.EncoderFolder = string.Empty;

        settings.TrackNaming = TrackNamingProfile.Generic;
        settings.EncodeConcurrency = 2;
        Assert.Equal(("Generic", 2), (settings.Compare.PersistedSettings.TrackNaming, settings.Compare.PersistedSettings.EncodeConcurrency));
        Assert.Equal(TrackNamingProfile.Generic, new SettingsViewModel(settings.Compare, Pages.Isolated(NothingRuns)).TrackNaming);

        settings.RestartCommand.Execute(null);
        Assert.Equal(["--page=settings"], Assert.Single(restarts));
        Assert.StartsWith("v", settings.Version, StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(settings.HarfBuzzVersion));
        Assert.False(string.IsNullOrEmpty(settings.SettingsPath));
        Assert.Equal(VideoQuality.Core.AppPaths.Root, settings.AppFolder);
    }

    [AvaloniaFact]
    public async Task Missing_tools_are_reported()
    {
        var settings = new SettingsViewModel(new MainViewModel(Pages.Isolated()), Pages.Isolated(NothingRuns));
        await settings.LocateToolsAsync();
        Assert.False(settings.HasMkvToolNix);
        Assert.False(settings.HasEncoder);
        Assert.Equal(Loc.T("settings.mkv.notFound"), settings.MkvToolNixStatus);
        Assert.Equal(Loc.T("settings.encoder.notFound"), settings.EncoderStatus);
    }

    [AvaloniaFact]
    public void Language_choice_is_saved_and_needs_a_restart_when_it_changes()
    {
        var compare = new MainViewModel(Pages.Isolated());
        compare.UpdateSettings(s => { s.Language = "zh-Hans"; s.TrackNaming = "bogus"; s.EncodeConcurrency = 9; });
        var settings = new SettingsViewModel(compare, Pages.Isolated(NothingRuns));
        Assert.Equal("zh-Hans", settings.SelectedLanguage.Code);
        Assert.Equal(TrackNamingProfile.GreenTea, settings.TrackNaming);
        Assert.Equal(4, settings.EncodeConcurrency);
        Assert.True(settings.NeedsRestart); // the tests run in English

        settings.SelectedLanguage = settings.LanguageOptions[0]; // follow the system
        Assert.Null(compare.PersistedSettings.Language);
        Assert.Equal(Loc.SystemLanguage != Loc.Language, settings.NeedsRestart);
        Assert.Equal(settings.SelectedLanguage.Name, settings.SelectedLanguage.ToString());

        compare.UpdateSettings(s => s.Language = "xx");
        Assert.Equal(string.Empty, new SettingsViewModel(compare, Pages.Isolated(NothingRuns)).SelectedLanguage.Code);
        compare.UpdateSettings(s => { s.Language = null; s.TrackNaming = null; s.EncodeConcurrency = 1; });
        compare.FlushSettings();
    }

    [Fact]
    public void Native_versions_degrade_to_not_available()
    {
        Assert.Equal("1.0", SettingsViewModel.NativeVersion(() => "1.0"));
        Assert.Equal(Loc.T("settings.notAvailable"), SettingsViewModel.NativeVersion(() => throw new DllNotFoundException()));
        Assert.Equal(Loc.T("settings.notAvailable"), SettingsViewModel.NativeVersion(() => throw new EntryPointNotFoundException()));
    }

    [AvaloniaFact]
    public async Task Settings_page_buttons_pick_folders_and_reveal_the_app()
    {
        var settings = new SettingsViewModel(new MainViewModel(Pages.Isolated()), Pages.Isolated(NothingRuns));
        var view = new SettingsView();
        var window = Ui.Show(view, settings);
        using var scope = new DialogScope(new ScriptedDialogs().Answer("/tools/mkv", null, "/tools/ffmpeg", null));

        Ui.Invoke(view, "OnPickMkvToolNix");
        Ui.Invoke(view, "OnPickMkvToolNix"); // cancelled
        Ui.Invoke(view, "OnPickEncoder");
        Ui.Invoke(view, "OnPickEncoder");
        Ui.Invoke(view, "OnOpenAppFolder");
        await Ui.SettleAsync();

        Assert.Equal(("/tools/mkv", "/tools/ffmpeg"), (settings.MkvToolNixFolder, settings.EncoderFolder));
        Assert.Equal([settings.AppFolder], scope.Dialogs.Revealed);
        window.Close();
    }
}

public class MediaInfoTests
{
    private static MediaInfo Release(string name, TimeSpan duration, bool mp4 = false) => new()
    {
        Path = "/r/" + name,
        Format = mp4 ? "mov,mp4,m4a,3gp,3g2,mj2" : "matroska,webm",
        FormatLongName = mp4 ? string.Empty : "Matroska / WebM",
        Duration = duration,
        Size = 400L * 1024 * 1024,
        BitRate = 2_000_000,
        Chapters = [new MediaChapter(TimeSpan.Zero, TimeSpan.FromMinutes(2), "OP")],
        Streams =
        [
            new MediaStream { Index = 0, Kind = StreamKind.Video, Codec = "hevc", BitDepth = 10, Width = 1920, Height = 1080, FrameRate = 23.976 },
            new MediaStream { Index = 1, Kind = StreamKind.Audio, Codec = "aac", BitRate = 256_000, SampleRate = 44100, Channels = 2, Language = "jpn" },
            new MediaStream { Index = 2, Kind = StreamKind.Subtitle, Codec = "ass", Language = "chi", Title = "JPSC", Default = true, Forced = true },
            new MediaStream { Index = 3, Kind = StreamKind.Subtitle, Codec = "ass", Language = "zh", Title = "JPTC" },
            new MediaStream { Index = 4, Kind = StreamKind.Attachment, FileName = "ABCDEFGH.ttf", MimeType = "font/ttf" },
            new MediaStream { Index = 5, Kind = StreamKind.Data, Codec = "bin_data" },
        ],
    };

    [AvaloniaTheory]
    [InlineData("[Studio GreenTea] Takopii No Genzai [01][WebRip][HEVC-10bit 1080p AAC ASSx2].mkv", 24, ReleaseProfile.WebEpisode, SubtitleDelivery.Embedded)]
    [InlineData("[Studio GreenTea] Takopii No Genzai [Movie][WebRip][HEVC-10bit 1080p AAC ASSx2].mkv", 24, ReleaseProfile.WebMovie, SubtitleDelivery.Embedded)]
    [InlineData("[Studio GreenTea] Takopii No Genzai [01][BDRip][HEVC-10bit 1080p FLAC ASSx2].mkv", 24, ReleaseProfile.BdEpisode, SubtitleDelivery.Embedded)]
    [InlineData("[Studio GreenTea] Takopii No Genzai [Movie][BDRip][HEVC-10bit 1080p FLAC ASSx2].mkv", 24, ReleaseProfile.BdMovie, SubtitleDelivery.Embedded)]
    [InlineData("whatever.mkv", 90, ReleaseProfile.WebMovie, SubtitleDelivery.Embedded)]
    [InlineData("whatever.mp4", 24, ReleaseProfile.WebEpisode, SubtitleDelivery.Burned)]
    public async Task Profile_and_delivery_follow_the_name_and_the_file(string name, int minutes, ReleaseProfile profile, SubtitleDelivery delivery)
    {
        var vm = new MediaInfoViewModel(path => Release(Path.GetFileName(path), TimeSpan.FromMinutes(minutes), path.EndsWith(".mp4", StringComparison.Ordinal)));
        await vm.OpenAsync("/r/" + name);
        Assert.Equal((profile, delivery), (vm.Profile, vm.Delivery));
        Assert.True(vm.HasInfo);
        Assert.False(vm.IsBusy);
        Assert.NotEmpty(vm.Findings);
        Assert.Contains(vm.Rows, r => r.Details.Contains("JPSC", StringComparison.Ordinal) && r.Details.Contains(Loc.T("track.flag.forced"), StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task Verdict_counts_fails_and_warnings()
    {
        var vm = new MediaInfoViewModel(path => Release(Path.GetFileName(path), TimeSpan.FromMinutes(24)) with { Streams = [.. Release("x", TimeSpan.Zero).Streams.Where(s => s.Kind != StreamKind.Data)] });
        await vm.OpenAsync("/r/[Studio GreenTea] Takopii No Genzai [01][WebRip][HEVC-10bit 1080p AAC ASSx2].mkv");
        Assert.Equal(Loc.T("mediainfo.verdict.pass"), vm.Verdict);

        vm.Profile = ReleaseProfile.BdEpisode; // AAC instead of FLAC: fails
        Assert.Equal(Loc.T("mediainfo.verdict.fail", vm.Findings.Count(f => f.IsFail), vm.Findings.Count(f => f.IsWarning)), vm.Verdict);

        var quiet = new MediaInfoViewModel(path => Release(Path.GetFileName(path), TimeSpan.FromMinutes(24)) with
        {
            Streams = [.. Release("x", TimeSpan.Zero).Streams.Where(s => s.Kind != StreamKind.Data).Select(s => s.Kind == StreamKind.Audio ? s with { BitRate = 200_000 } : s)],
        });
        await quiet.OpenAsync("/r/[Studio GreenTea] Takopii No Genzai [01][WebRip][HEVC-10bit 1080p AAC ASSx2].mkv");
        Assert.Equal(Loc.T("mediainfo.verdict.warn", 1), quiet.Verdict); // a quiet AAC track only warns
        Assert.Contains(quiet.Findings, f => f.IsWarning);
        Assert.Contains(vm.Findings, f => f.IsPass);
    }

    [AvaloniaFact]
    public async Task Unreadable_files_clear_the_report()
    {
        var vm = new MediaInfoViewModel(path => path.EndsWith("bad", StringComparison.Ordinal)
            ? throw new InvalidOperationException("cannot open")
            : Release("whatever.mkv", TimeSpan.FromMinutes(24)) with { Chapters = [] });
        await vm.OpenAsync("/r/good.mkv");
        Assert.DoesNotContain(vm.Rows, r => r.Icon == FluentIcons.Common.Symbol.BookOpen);
        await vm.OpenAsync("/r/bad");
        Assert.Equal("cannot open", vm.Status);
        Assert.False(vm.HasInfo);
        Assert.Empty(vm.Rows);
        Assert.Empty(vm.Findings);
        Assert.Equal(string.Empty, vm.Verdict);
        Assert.Equal(string.Empty, vm.Text);
        vm.Check(); // nothing open: nothing to check
        Assert.Empty(vm.Findings);
    }

    [AvaloniaFact]
    public async Task Real_files_open_from_the_page_and_copy_to_the_clipboard()
    {
        var vm = new MediaInfoViewModel();
        var view = new MediaInfoView();
        var window = Ui.Show(view, vm);
        string clip = await TestMedia.ClipAsync();
        using var scope = new DialogScope(new ScriptedDialogs().Answer(new[] { clip }, null));

        Ui.Invoke(view, "OnOpen");
        await Ui.UntilAsync(() => vm.HasInfo);
        Ui.Invoke(view, "OnOpen"); // cancelled
        Assert.Contains("h264", vm.Text, StringComparison.Ordinal);

        vm.CopyCommand.Execute(null);
        await Ui.SettleAsync();
        Assert.Equal(vm.Text, await Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(window.Clipboard!));

        string rich = await TestMedia.RichAsync();
        Ui.Drop(window, view, rich);
        await Ui.UntilAsync(() => vm.Path == rich && !vm.IsBusy);
        Assert.Contains(vm.Rows, r => r.Icon == FluentIcons.Common.Symbol.TextFont);

        view.DataContext = null; // detaching the page from its model is allowed
        window.Close();
    }
}
