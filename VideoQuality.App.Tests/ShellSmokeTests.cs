using Avalonia.Headless.XUnit;
using VideoQuality.App.ViewModels;
using VideoQuality.App.Views;

namespace VideoQuality.App.Tests;

public class ShellSmokeTests
{
    [AvaloniaFact]
    public void Main_window_shows_every_page()
    {
        var compare = new MainViewModel();
        var shell = new ShellViewModel(compare);
        var window = new MainWindow { DataContext = shell };
        window.Show();

        foreach (NavPage page in shell.Pages.Append(shell.SettingsPage))
        {
            shell.Navigate(page.Key);
            Assert.Equal(page.Key, shell.SelectedPage?.Key);
        }

        window.Close();
    }
}
