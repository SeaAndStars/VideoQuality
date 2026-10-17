using VideoQuality.Core.Scoring;
using VideoQuality.Core.Settings;

namespace VideoQuality.Tests;

public sealed class SettingsFileTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vq-settings-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class Sample
    {
        public string Model { get; set; } = "default";

        public VmafBackend Backend { get; set; }

        public string? FfmpegPath { get; set; }
    }

    [Fact]
    public void Round_trips_through_the_first_location()
    {
        string portable = Path.Combine(_root, "app", "settings.json");
        string user = Path.Combine(_root, "user", "settings.json");

        string? written = SettingsFile.Save(new Sample { Model = "vmaf_v0.6.1", Backend = VmafBackend.Cpu }, [portable, user]);
        Sample loaded = SettingsFile.Load<Sample>([portable, user]);

        Assert.Equal(portable, written);
        Assert.Equal("vmaf_v0.6.1", loaded.Model);
        Assert.Equal(VmafBackend.Cpu, loaded.Backend);
        Assert.Contains("\"backend\": \"Cpu\"", File.ReadAllText(portable));
        Assert.DoesNotContain("ffmpegPath", File.ReadAllText(portable)); // nulls are omitted
    }

    [Fact]
    public void Falls_back_when_the_app_folder_is_not_writable()
    {
        // A file where the portable directory should be makes the portable location unwritable.
        string blocker = Path.Combine(_root, "app");
        File.WriteAllText(blocker, "not a directory");
        string user = Path.Combine(_root, "user", "settings.json");

        string? written = SettingsFile.Save(new Sample { Model = "x" }, [Path.Combine(blocker, "settings.json"), user]);

        Assert.Equal(user, written);
        Assert.Equal("x", SettingsFile.Load<Sample>([Path.Combine(blocker, "settings.json"), user]).Model);
    }

    [Fact]
    public void Missing_or_corrupt_files_give_defaults()
    {
        string path = Path.Combine(_root, "settings.json");
        Assert.Equal("default", SettingsFile.Load<Sample>([path]).Model);

        File.WriteAllText(path, "{ not json");
        Assert.Equal("default", SettingsFile.Load<Sample>([path]).Model);
    }
}
