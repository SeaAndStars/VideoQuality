using Avalonia.Headless.XUnit;
using VideoQuality.App.ViewModels;
using VideoQuality.App.Views;

namespace VideoQuality.App.Tests;

public class ShellSmokeTests
{
    [AvaloniaFact]
    public void Main_window_shows_every_page_and_never_a_separator()
    {
        var compare = new MainViewModel(Pages.Isolated());
        var shell = new ShellViewModel(compare);
        var window = new MainWindow { DataContext = shell };
        window.Show();

        foreach (NavPage page in shell.Pages.Where(p => !p.IsSeparator).Append(shell.SettingsPage))
        {
            shell.Navigate(page.Key);
            Assert.Equal(page.Key, shell.PageKey);
        }

        NavPage separator = shell.Pages.First(p => p.IsSeparator);
        shell.Navigate(separator.Key);
        Assert.Equal("settings", shell.PageKey);
        shell.SelectedPage = separator;
        Assert.Equal("settings", shell.PageKey);

        window.Close();
    }
}
