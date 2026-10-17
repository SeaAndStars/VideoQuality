using Avalonia.Controls;
using Ursa.Controls;
using VideoQuality.App.ViewModels;

namespace VideoQuality.App.Views;

/// <summary>
/// Startup splash. It stays up while the real startup work runs (loading libav, resolving and
/// probing ffmpeg), for at least <see cref="SplashWindow.CountDown"/>, then opens the main window.
/// </summary>
public partial class SplashView : SplashWindow
{
    private readonly Func<Window> _createMainWindow;

    public SplashView()
        : this(new SplashViewModel(), () => new Window())
    {
    }

    public SplashView(SplashViewModel viewModel, Func<Window> createMainWindow)
    {
        InitializeComponent();
        DataContext = viewModel;
        _createMainWindow = createMainWindow;
        CountDown = TimeSpan.FromMilliseconds(1200);
    }

    private SplashViewModel ViewModel => (SplashViewModel)DataContext!;

    protected override async Task<bool> CanClose()
    {
        // Never hold the app hostage: a hung probe still lets the main window open (it reports
        // the problem itself).
        await Task.WhenAny(ViewModel.Startup, Task.Delay(TimeSpan.FromSeconds(20))).ConfigureAwait(true);
        return true;
    }

    protected override Task<Window?> CreateNextWindow() => Task.FromResult<Window?>(_createMainWindow());
}
