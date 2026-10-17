using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using VideoQuality.App.ViewModels;
using VideoQuality.App.Views;
using VideoQuality.Core.Encode;

namespace VideoQuality.App.Tests;

public class EncodeViewTests
{
    [AvaloniaFact]
    public void Preset_editor_combo_boxes_show_the_preset_values()
    {
        var shell = new ShellViewModel(new MainViewModel());
        var window = new Window { Width = 1400, Height = 900, Content = new EncodeView { DataContext = shell.Encode } };
        window.Show();

        List<ComboBox> combos = window.GetVisualDescendants().OfType<ComboBox>().ToList();
        ComboBox mode = combos.Single(c => ReferenceEquals(c.ItemsSource, shell.Encode.Editor.Modes));
        ComboBox preset = combos.Single(c => ReferenceEquals(c.ItemsSource, shell.Encode.Editor.EncoderPresets));

        // The encoder ffmpeg is located after the page exists: the encoder list is replaced.
        shell.Settings.Encoder = new Core.Tools.EncoderTool("ffmpeg", "ffmpeg version x",
            new HashSet<string> { "libx265", "libx264", "aac" }, new HashSet<string> { "subtitles" });
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Equal("libx265", shell.Encode.Editor.Encoder.Name);
        Assert.Equal(RateControl.Quality, shell.Encode.Editor.Mode);
        Assert.Equal(RateControl.Quality, mode.SelectedItem);
        Assert.Equal("slow", shell.Encode.Editor.EncoderPreset);
        Assert.Equal("slow", preset.SelectedItem);
        window.Close();
    }
}
