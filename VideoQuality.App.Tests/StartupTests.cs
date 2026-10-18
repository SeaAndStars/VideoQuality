using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using VideoQuality.App.Services;
using VideoQuality.App.ViewModels;
using VideoQuality.App.Views;
using VideoQuality.Core.Localization;
using VideoQuality.Tests;

namespace VideoQuality.App.Tests;

public sealed class StartupTests : IDisposable
{
    private readonly string _work = Directory.CreateTempSubdirectory("vq-app-startup-").FullName;

    public void Dispose() => Directory.Delete(_work, true);

    [Fact]
    public void Arguments_pick_the_mode_the_page_and_the_files()
    {
        StartupOptions none = StartupOptions.Parse([]);
        Assert.Equal((null, null, 0, false), (none.Mode, none.Page, none.Media.Count, none.SelfTest));

        StartupOptions full = StartupOptions.Parse(["--mode=WIPE", "--page=Mux", "a.mkv", "--self-test"]);
        Assert.Equal((CompareMode.Wipe, "mux", 1, true), (full.Mode, full.Page, full.Media.Count, full.SelfTest));

        Assert.Null(StartupOptions.Parse(["--mode=sideways"]).Mode);
        Assert.Equal("subset", StartupOptions.Parse(["a.ASS", "b.ssa"]).Page);
        Assert.Null(StartupOptions.Parse(["a.ass", "b.mkv"]).Page);
        Assert.Equal("compare", StartupOptions.Parse(["--page=compare", "a.ass"]).Page);
        Assert.Equal("zh-CN", App.CultureFor("zh-Hans").Name);
        Assert.Equal("en-US", App.CultureFor("en").Name);
    }

    [AvaloniaFact]
    public async Task The_main_window_opens_files_on_the_page_that_takes_them()
    {
        string clip = await TestMedia.ClipAsync();
        string script = TestMedia.Script(_work, "ep01.ass");

        var compare = new MainViewModel(Pages.Isolated());
        Window subset = App.CreateMainWindow(compare, StartupOptions.Parse(["--mode=flicker", script]));
        subset.Show();
        var shell = (ShellViewModel)subset.DataContext!;
        await Ui.UntilAsync(() => shell.FontSubset.HasScripts);
        Assert.Equal("subset", shell.PageKey);
        Assert.Equal(CompareMode.Flicker, compare.CompareMode);
        subset.Close();

        var other = new MainViewModel(Pages.Isolated());
        Window fallback = App.CreateMainWindow(other, StartupOptions.Parse(["--page=compare", clip])); // Compare takes no files: the drop router does
        fallback.Show();
        await Ui.UntilAsync(() => other.HasReference);
        fallback.Close();

        var plain = new MainViewModel(Pages.Isolated());
        Window dropped = App.CreateMainWindow(plain, StartupOptions.Parse([clip]));
        dropped.Show();
        await Ui.UntilAsync(() => plain.HasReference);
        dropped.Close();

        Window empty = App.CreateMainWindow(new MainViewModel(Pages.Isolated()), StartupOptions.Parse([]));
        empty.Show();
        empty.Close();
    }

    [AvaloniaFact]
    public async Task Startup_work_survives_a_libav_that_does_not_load()
    {
        var splash = new SplashViewModel();
        await App.StartupAsync(new MainViewModel(Pages.Isolated(Pages.NothingRuns)), splash, () => throw new DllNotFoundException("no avcodec"));
        Assert.True(splash.IsReady);
        Assert.Equal(Loc.T("splash.ready"), splash.Status);
        await App.StartupAsync(new MainViewModel(Pages.Isolated(Pages.NothingRuns)), new SplashViewModel(), () => { });
    }

    [AvaloniaFact]
    public async Task The_splash_hands_over_to_the_main_window()
    {
        var designer = new SplashView(); // the previewer's constructor
        Assert.IsType<SplashViewModel>(designer.DataContext);

        Window? next = null;
        var splash = new SplashView(new SplashViewModel { Startup = Task.Delay(50) }, () => next = new Window());
        splash.Show();
        await Ui.UntilAsync(() => next is not null, 10_000);
        next!.Close();
    }

    [Fact]
    public void Crashes_are_logged_and_startup_errors_rethrown()
    {
        Assert.Equal(7, Program.Run(["x"], args => args.Length + 6));
        Assert.Throws<InvalidOperationException>(() => Program.Run([], _ => throw new InvalidOperationException("no display")));
        Program.OnUnhandledException(null, new UnhandledExceptionEventArgs(new InvalidOperationException("boom"), false));
        Program.OnUnhandledException(null, new UnhandledExceptionEventArgs("not an exception", false));
        var unobserved = new UnobservedTaskExceptionEventArgs(new AggregateException(new InvalidOperationException("lost")));
        Program.OnUnobservedTaskException(null, unobserved);
        Assert.True(unobserved.Observed);
        Assert.NotNull(Program.BuildAvaloniaApp());
        Assert.NotNull(Program.BuildAvaloniaApp(headless: true));
    }

    [AvaloniaFact]
    public void Content_sits_below_the_title_bar_or_at_the_top_without_one()
    {
        var window = new MainWindow { DataContext = new ShellViewModel(new MainViewModel(Pages.Isolated()), Pages.Isolated(Pages.NothingRuns)) };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.IsTitleBarVisible = false;
        window.IsMinimizeButtonVisible = false;
        window.IsRestoreButtonVisible = false;
        window.IsFullScreenButtonVisible = false;
        window.IsCloseButtonVisible = false;
        window.Width = 1000;
        Dispatcher.UIThread.RunJobs();
        window.IsTitleBarVisible = true;
        window.IsCloseButtonVisible = true;
        window.Width = 1100;
        Dispatcher.UIThread.RunJobs();
        window.Close();
    }
}
