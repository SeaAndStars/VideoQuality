using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(VideoQuality.App.Tests.TestApp))]

namespace VideoQuality.App.Tests;

/// <summary>The real App (themes, resources, styles) on the headless platform with Skia drawing.</summary>
public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp()
    {
        // Tests change settings; start every run from defaults (English, no saved folders).
        File.Delete(VideoQuality.Core.Settings.SettingsFile.PortablePath);
        return AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    }
}
