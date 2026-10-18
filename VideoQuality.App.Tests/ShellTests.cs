using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using VideoQuality.App.Services;
using VideoQuality.App.ViewModels;
using VideoQuality.App.Views;
using VideoQuality.Core.Encode;
using VideoQuality.Core.Localization;
using VideoQuality.Core.Tools;
using VideoQuality.Tests;

namespace VideoQuality.App.Tests;

public sealed class ShellTests : IDisposable
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures", "fonts");
    private readonly string _work = Directory.CreateTempSubdirectory("vq-app-shell-").FullName;

    public void Dispose() => Directory.Delete(_work, true);

    private static async Task<ShellViewModel> ShellAsync(IProcessRunner? runner = null)
    {
        AppServices services = Pages.Isolated(runner);
        var shell = new ShellViewModel(new MainViewModel(services), services);
        await Ui.UntilAsync(() => shell.Settings.MkvToolNixStatus != Loc.T("settings.mkv.looking") && shell.Settings.EncoderStatus != Loc.T("settings.encoder.looking"));
        return shell;
    }

    [AvaloniaFact]
    public async Task Navigation_titles_and_unknown_pages()
    {
        ShellViewModel shell = await ShellAsync(Pages.NothingRuns);
        Assert.True(shell.IsCompare);
        Assert.Equal(shell.Compare.WindowTitle, shell.WindowTitle);
        Assert.Equal(shell.SelectedPage!.Title, shell.TitleText); // nothing to compare yet

        shell.Navigate("settings");
        Assert.True(shell.IsSettings);
        shell.Navigate("nope");
        Assert.True(shell.IsSettings);
        shell.Navigate("mux");
        Assert.Equal($"{Loc.T("nav.mux")} — VideoQuality", shell.WindowTitle);
        Assert.Equal(Loc.T("nav.mux"), shell.TitleText);

        shell.SelectedPage = null;
        Assert.Equal((string.Empty, string.Empty), (shell.PageKey, shell.TitleText));
        Assert.Equal(" — VideoQuality", shell.WindowTitle);

        var changed = new List<string?>();
        shell.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        shell.IsNavCollapsed = true;
        Assert.Contains(nameof(ShellViewModel.IsNavCollapsed), changed);
        Assert.Same(AppServices.Default, new ShellViewModel(new MainViewModel(Pages.Isolated())).Services);
    }

    [AvaloniaFact]
    public async Task Startup_files_go_to_the_page_that_takes_them()
    {
        ShellViewModel shell = await ShellAsync();
        string clip = await TestMedia.ClipAsync();
        string script = TestMedia.Script(_work, "ep01.JPSC.ass");
        string chapters = Path.Combine(_work, "c.txt");
        File.WriteAllText(chapters, "CHAPTER01=00:00:00.000\nCHAPTER01NAME=OP\n");

        Assert.True(await shell.OpenFilesAsync("subset", [script]));
        Assert.Single(shell.FontSubset.Scripts);
        Assert.True(await shell.OpenFilesAsync("extract", [null, clip]));
        Assert.Equal(clip, shell.Extract.Source);
        Assert.True(await shell.OpenFilesAsync("mux", [clip]));
        Assert.True(shell.Mux.HasInputs);
        Assert.True(await shell.OpenFilesAsync("pipeline", [clip]));
        Assert.Equal(clip, shell.Pipeline.Video);
        Assert.True(await shell.OpenFilesAsync("encode", [clip]));
        Assert.True(shell.Encode.HasInputs);
        Assert.True(await shell.OpenFilesAsync("mediainfo", [clip]));
        Assert.True(shell.MediaInfo.HasInfo);
        Assert.True(await shell.OpenFilesAsync("trim", [clip]));
        Assert.True(shell.TrimMerge.HasSource);
        Assert.True(await shell.OpenFilesAsync("chapters", [chapters]));
        Assert.True(shell.Chapters.HasRows);
        Assert.True(await shell.OpenFilesAsync("naming", [clip]));
        Assert.Equal("naming", shell.PageKey);

        Assert.False(await shell.OpenFilesAsync("compare", [clip]));
        Assert.False(await shell.OpenFilesAsync("extract", []));
        Assert.False(await shell.OpenFilesAsync("mediainfo", [null]));
        Assert.False(await shell.OpenFilesAsync("trim", []));
        Assert.False(await shell.OpenFilesAsync("chapters", []));
        Assert.False(await shell.OpenFilesAsync("naming", []));
    }

    [AvaloniaFact]
    public async Task Tools_are_looked_for_again_when_a_page_needs_them()
    {
        ShellViewModel shell = await ShellAsync(Pages.NothingRuns);
        Assert.Null(shell.Settings.MkvToolNix);
        Assert.True(await shell.OpenFilesAsync("trim", [Path.Combine(_work, "missing.mkv")]));
        Assert.Null(shell.Settings.MkvToolNix);
    }

    [AvaloniaFact]
    public async Task Pages_hand_work_to_each_other_and_report_through_toasts()
    {
        ShellViewModel shell = await ShellAsync();
        var window = new MainWindow { DataContext = shell };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        using var scope = new DialogScope(new ScriptedDialogs());

        // Font Subset → Mux
        string fonts = Directory.CreateDirectory(Path.Combine(_work, "fonts")).FullName;
        File.Copy(Path.Combine(Fixtures, "DejaVuSans.ttf"), Path.Combine(fonts, "DejaVuSans.ttf"));
        string script = Path.Combine(_work, "ep01.JPSC.ass");
        File.WriteAllText(script, "[V4+ Styles]\nFormat: Name, Fontname\nStyle: Default,DejaVu Sans\n\n[Events]\nFormat: Style, Text\nDialogue: Default,hello");
        shell.FontSubset.AddPaths([script]);
        shell.FontSubset.FontDirectory = fonts;
        await shell.FontSubset.RunCommand.ExecuteAsync(null);
        shell.FontSubset.SendToMuxCommand.Execute(null);
        await Ui.UntilAsync(() => shell.Mux.Attachments.Count == 1 && shell.Mux.HasInputs);
        Assert.Equal("mux", shell.PageKey);

        // Chapters → Mux
        shell.Navigate("chapters");
        shell.Chapters.AddCommand.Execute(null);
        shell.Chapters.SendToMuxCommand.Execute(null);
        Assert.Equal("mux", shell.PageKey);
        Assert.EndsWith(".xml", shell.Mux.Chapters, StringComparison.Ordinal);

        // Encode → Compare, and Reveal through the window
        string clip = await TestMedia.ClipAsync();
        var row = new EncodeJobRow(new EncodeJob(clip, new EncodeCommand([["-i", clip]], clip, [], [], 0, TimeSpan.FromSeconds(2))));
        shell.Encode.ScoreInCompareCommand.Execute(row);
        await Ui.UntilAsync(() => shell.Compare.HasReference);
        Assert.True(shell.IsCompare);
        Assert.Equal(shell.Compare.ComparisonTitle, shell.TitleText);
        shell.Encode.RevealCommand.Execute(row);
        await Ui.SettleAsync();
        Assert.Equal([clip], scope.Dialogs.Revealed);

        // Pipeline → Compare
        shell.Navigate("pipeline");
        shell.Pipeline.EncodedFile = clip;
        await shell.Pipeline.OpenInCompareCommand.ExecuteAsync(null);
        Assert.True(shell.IsCompare);

        // Every page's toasts reach the window.
        shell.Encode.StartCommand.Execute(null); // Encode (no inputs is fine: an encoder is there, nothing queued)
        shell.Settings.Encoder = null;
        await shell.Encode.AddPathsAsync([clip]);
        shell.Encode.StartCommand.Execute(null);
        string junk = Path.Combine(_work, "junk.mkv");
        File.WriteAllText(junk, "not media");
        await shell.TrimMerge.AddMergeInputsAsync([junk]);
        shell.FontSubset.OutputDirectory = _work;
        await shell.FontSubset.RunCommand.ExecuteAsync(null);
        shell.Extract.Settings.MkvToolNix = ScriptedMkv.Create(r => r.Arguments[0] == "-J"
            ? ScriptedRunner.Ok(ScriptedMkv.Identify("Matroska", 1, 0, [(0, "video", "AVC", "", "")]))
            : new ProcessResult(2, string.Empty, "nope"));
        await shell.Extract.OpenAsync(junk);
        shell.Extract.SelectKindCommand.Execute("all");
        await shell.Extract.ExtractCommand.ExecuteAsync(null);
        shell.Mux.Output = Path.Combine(_work, "out.mkv");
        await shell.Mux.MuxCommand.ExecuteAsync(null);
        shell.Pipeline.SetVideo(junk);
        shell.Settings.Encoder = new EncoderTool("ffmpeg", "ffmpeg version x", new HashSet<string>(), new HashSet<string>());
        await shell.Pipeline.RunCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        // The window: rail toggle, the settings entry, reveal, closing.
        Ui.Invoke(window, "OnToggleRail");
        Assert.True(shell.IsNavCollapsed);
        MethodInfo selected = typeof(MainWindow).GetMethod("OnSettingsNavSelected", BindingFlags.Instance | BindingFlags.NonPublic)!;
        selected.Invoke(window, [null, new SelectionChangedEventArgs(Avalonia.Controls.Primitives.SelectingItemsControl.SelectionChangedEvent, Array.Empty<object>(), Array.Empty<object>())]);
        Assert.True(shell.IsCompare);
        selected.Invoke(window, [null, new SelectionChangedEventArgs(Avalonia.Controls.Primitives.SelectingItemsControl.SelectionChangedEvent, Array.Empty<object>(), new object[] { shell.SettingsPage })]);
        Assert.True(shell.IsSettings);

        window.Width = 900; // re-fits below the title bar
        Dispatcher.UIThread.RunJobs();
        window.DataContext = null;
        window.DataContext = shell;
        window.Close();
    }

    [AvaloniaFact]
    public async Task Closing_stops_encodes_and_saves()
    {
        var release = new TaskCompletionSource<ProcessResult>();
        var runner = new ScriptedRunner((request, token) => request.Arguments.Contains("-i")
            ? release.Task.WaitAsync(token)
            : Task.FromResult(new ProcessResult(1, string.Empty, string.Empty)));
        ShellViewModel shell = await ShellAsync(runner);
        var job = new EncodeJob("/in.mkv", new EncodeCommand([["-i", "x"]], Path.Combine(_work, "o.mkv"), [], [], 0, TimeSpan.FromSeconds(1)));
        shell.Queue.Add(job);
        await Ui.UntilAsync(() => job.State == EncodeJobState.Running);
        shell.Dispose();
        await shell.Queue.WhenIdleAsync();
        Assert.Equal(EncodeJobState.Cancelled, job.State);
    }
}
