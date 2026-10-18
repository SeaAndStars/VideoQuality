using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using VideoQuality.App.ViewModels;
using VideoQuality.App.Views;
using VideoQuality.Core.Localization;

namespace VideoQuality.App.Tests;

/// <summary>
/// Renders every page for design review: <c>VQ_SCREENSHOTS=/dir [VQ_SCREENSHOT_LANG=zh-Hans]
/// dotnet test --filter Screenshots</c>. Skipped otherwise.
/// </summary>
public class Screenshots
{
    [AvaloniaFact]
    public async Task Every_page()
    {
        string? folder = Environment.GetEnvironmentVariable("VQ_SCREENSHOTS");
        Assert.SkipWhen(string.IsNullOrEmpty(folder), "set VQ_SCREENSHOTS to render the pages");
        string language = Environment.GetEnvironmentVariable("VQ_SCREENSHOT_LANG") ?? "en";
        Loc.SetLanguage(language);
        Directory.CreateDirectory(folder!);

        var services = Pages.Isolated();
        var shell = new ShellViewModel(new MainViewModel(services), services);
        var window = new MainWindow { DataContext = shell, Width = 1600, Height = 1000 };
        window.Show();
        await Ui.SettleAsync(1500);

        foreach (NavPage page in shell.Pages.Where(p => !p.IsSeparator).Append(shell.SettingsPage))
        {
            shell.Navigate(page.Key);
            await Ui.SettleAsync(400);
            Dispatcher.UIThread.RunJobs();
            using var frame = window.CaptureRenderedFrame()!;
            frame.Save(Path.Combine(folder!, $"{language}-{page.Key}.png"));
        }

        Loc.SetLanguage("en");
    }
}
