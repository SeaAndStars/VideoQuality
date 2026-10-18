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

    [Fact]
    public void A_null_document_gives_defaults_and_nowhere_writable_saves_nothing()
    {
        string path = Path.Combine(_root, "null.json");
        File.WriteAllText(path, "null");
        Assert.Equal("default", SettingsFile.Load<Sample>([path]).Model);

        string blocker = Path.Combine(_root, "file");
        File.WriteAllText(blocker, string.Empty);
        Assert.Null(SettingsFile.Save(new Sample(), [Path.Combine(blocker, "settings.json")]));
        Assert.Null(SettingsFile.Save(new Sample(), []));
    }

    [Fact]
    public void User_folder_follows_the_platform()
    {
        Assert.EndsWith(Path.Combine("VideoQuality", "settings.json"), SettingsFile.UserPathFor(true, null), StringComparison.Ordinal);
        Assert.Equal(Path.Combine("/cfg", "videoquality", "settings.json"), SettingsFile.UserPathFor(false, "/cfg"));
        Assert.EndsWith(Path.Combine(".config", "videoquality", "settings.json"), SettingsFile.UserPathFor(false, " "), StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(SettingsFile.UserPath));
    }

    [Fact]
    public void Location_is_the_portable_file_until_one_is_found()
    {
        string? current = SettingsFile.CurrentPath;
        SettingsFile.CurrentPath = null;
        Assert.Equal(SettingsFile.PortablePath, SettingsFile.Location);
        SettingsFile.CurrentPath = "/x/settings.json";
        Assert.Equal("/x/settings.json", SettingsFile.Location);
        SettingsFile.CurrentPath = current;
    }
}
