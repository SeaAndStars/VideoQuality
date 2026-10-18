using System.Diagnostics;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using FluentIcons.Common;
using VideoQuality.App.Controls;
using VideoQuality.App.Converters;
using VideoQuality.App.Services;
using VideoQuality.App.ViewModels;
using VideoQuality.Core.Encode;
using VideoQuality.Core.Imaging;
using VideoQuality.Core.Localization;
using VideoQuality.Core.Scoring;

namespace VideoQuality.App.Tests;

public class SmallPiecesTests
{
    [Fact]
    public void Labels_cover_every_enum_and_fall_back_to_text()
    {
        var labels = new LabelConverter();
        object? Label(object? value) => labels.Convert(value, typeof(string), null, CultureInfo.InvariantCulture);

        foreach (CompareMode mode in Enum.GetValues<CompareMode>())
        {
            Assert.False(string.IsNullOrEmpty((string?)Label(mode)));
        }

        Assert.Equal(Loc.T("label.channel.luma"), Label(DifferenceChannels.Luma));
        Assert.Equal(Loc.T("label.channel.chroma"), Label(DifferenceChannels.Chroma));
        Assert.Equal(Loc.T("label.channel.both"), Label(DifferenceChannels.Both));
        Assert.Equal(Loc.T("label.backend.auto"), Label(VmafBackend.Auto));
        Assert.Equal("CPU", Label(VmafBackend.Cpu));
        Assert.Equal("CUDA", Label(VmafBackend.Cuda));
        Assert.Equal("VMAF NEG", Label(VmafModels.Neg));
        Assert.Equal(Loc.T("label.RateControl.Quality"), Label(RateControl.Quality));
        Assert.Equal("Monday", Label(DayOfWeek.Monday)); // no label: the name
        Assert.Equal("42", Label(42));
        Assert.Null(Label(null));
        Assert.Throws<NotSupportedException>(() => labels.ConvertBack("x", typeof(object), null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Mode_icons_and_selection_keeping()
    {
        var icons = new ModeIconConverter();
        Assert.Equal(Symbol.SplitVertical, icons.Convert(CompareMode.SideBySide, typeof(object), null, CultureInfo.InvariantCulture));
        Assert.Equal(Symbol.ArrowAutofitWidth, icons.Convert(CompareMode.Wipe, typeof(object), null, CultureInfo.InvariantCulture));
        Assert.Equal(Symbol.Flash, icons.Convert(CompareMode.Flicker, typeof(object), null, CultureInfo.InvariantCulture));
        Assert.Equal(Symbol.ShapeExclude, icons.Convert(CompareMode.Difference, typeof(object), null, CultureInfo.InvariantCulture));
        Assert.Equal(Symbol.Square, icons.Convert(null, typeof(object), null, CultureInfo.InvariantCulture));
        Assert.Throws<NotSupportedException>(() => icons.ConvertBack(null, typeof(object), null, CultureInfo.InvariantCulture));

        KeepSelectionConverter keep = KeepSelectionConverter.Instance;
        Assert.Equal("a", keep.Convert("a", typeof(object), null, CultureInfo.InvariantCulture));
        Assert.Equal("a", keep.ConvertBack("a", typeof(object), null, CultureInfo.InvariantCulture));
        Assert.Same(BindingOperations.DoNothing, keep.ConvertBack(null, typeof(object), null, CultureInfo.InvariantCulture));

        Assert.Equal("b.mkv", PathConverters.FileName.Convert("/a/b.mkv", typeof(string), null, CultureInfo.InvariantCulture));
    }

    [AvaloniaFact]
    public void Restart_starts_the_app_again_and_shuts_this_one_down()
    {
        Action<ProcessStartInfo> start = AppServices.StartProcess;
        Action shutdown = AppServices.ShutdownApp;
        try
        {
            ProcessStartInfo? started = null;
            bool closed = false;
            AppServices.StartProcess = info => started = info;
            AppServices.ShutdownApp = () => closed = true;
            AppServices.Default.Restart(["--page=settings"]);
            Assert.Equal("--page=settings", started!.ArgumentList[^1]);
            Assert.False(started.UseShellExecute);
            Assert.True(closed);

            AppServices.ShutdownDesktop(); // the headless lifetime is not a desktop one: nothing happens
            AppServices.StartDetached(new ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : "true") { UseShellExecute = false, ArgumentList = { } });
        }
        finally
        {
            AppServices.StartProcess = start;
            AppServices.ShutdownApp = shutdown;
        }
    }

    [AvaloniaFact]
    public async Task Without_a_window_toasts_are_dropped_and_questions_answer_yes()
    {
        ToastHost.Detach();
        ToastHost.Show(new ToastRequest(ToastKind.Info, "nobody sees this"));
        Assert.True(await ToastHost.ConfirmAsync("t", "m"));
    }

    [Fact]
    public void Local_paths_of_storage_items()
    {
        Assert.Null(StorageDialogs.LocalPath(null));
        string temp = Path.GetTempPath();
        Assert.Equal(temp.TrimEnd(Path.DirectorySeparatorChar), StorageDialogs.LocalPath(Storage.Folder(temp))!.TrimEnd(Path.DirectorySeparatorChar));
        Assert.Null(StorageDialogs.FolderOf(string.Empty));
    }

    [AvaloniaFact]
    public void Splash_and_sparkline_properties()
    {
        var splash = new SplashViewModel { Status = "x", IsReady = true };
        Assert.Equal(AppInfo.Version, splash.Version);
        Assert.True(splash.Startup.IsCompleted);
        var line = new Sparkline { Stroke = Avalonia.Media.Brushes.Red };
        Assert.Same(Avalonia.Media.Brushes.Red, line.Stroke);
    }

    [AvaloniaFact]
    public async Task Pages_without_listeners_do_nothing_when_asked_to_hand_off()
    {
        new MediaInfoViewModel().CopyCommand.Execute(null);
        var naming = new NamingViewModel();
        naming.CopyCommand.Execute("x");
        naming.CopyAllCommand.Execute(null);
        var chapters = new ChaptersViewModel();
        chapters.AddCommand.Execute(null);
        chapters.SendToMuxCommand.Execute(null);

        var subset = new FontSubsetViewModel();
        string folder = Directory.CreateTempSubdirectory("vq-busy-").FullName;
        File.WriteAllText(Path.Combine(folder, "a.ass"), "[Events]\nFormat: Style, Text\nDialogue: x,a");
        subset.AddPaths([Path.Combine(folder, "a.ass")]);
        subset.OutputDirectory = Path.Combine(folder, "out");
        Task run = subset.RunCommand.ExecuteAsync(null);
        Assert.False(subset.RunCommand.CanExecute(null)); // busy
        await run;
        Directory.Delete(folder, true);

        var settings = new SettingsViewModel(new MainViewModel()); // default services
        await Ui.UntilAsync(() => settings.MkvToolNixStatus != Loc.T("settings.mkv.looking"));
        Assert.Same(AppServices.Default, settings.Services);
    }
}
