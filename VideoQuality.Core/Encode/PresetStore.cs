using System.Text.Json;
using System.Text.Json.Serialization;
using VideoQuality.Core.Diagnostics;
using VideoQuality.Core.Release;

namespace VideoQuality.Core.Encode;

/// <summary>
/// Built-in presets plus the user's own, saved one JSON file each in a folder (portable: beside the
/// app, so presets travel with it and can be shared by copying a file).
/// </summary>
public sealed class PresetStore(string directory)
{
    public static string DefaultDirectory => Path.Combine(AppPaths.Root, "presets");

    public string Directory { get; } = directory;

    /// <summary>The GreenTea presets first, then general-purpose ones.</summary>
    public static IReadOnlyList<EncodePreset> BuiltIn { get; } =
    [
        .. GreenTeaSpec.Presets,
        new EncodePreset
        {
            Name = "x264 compatible MP4",
            Container = "mp4",
            Video = new VideoSettings { Encoder = "libx264", Quality = 20, Preset = "slow", BitDepth = 8 },
            Audio = new AudioSettings { Encoder = "aac", BitrateKbps = 192 },
            BuiltIn = true,
        },
        new EncodePreset
        {
            Name = "NVENC HEVC fast",
            Video = new VideoSettings { Encoder = "hevc_nvenc", Quality = 26, Preset = "p5", BitDepth = 10 },
            Audio = new AudioSettings { Encoder = "copy" },
            Streams = new StreamSettings { KeepSubtitles = true, KeepAttachments = true },
            BuiltIn = true,
        },
        new EncodePreset
        {
            Name = "SVT-AV1 archive",
            Video = new VideoSettings { Encoder = "libsvtav1", Quality = 28, Preset = "4", BitDepth = 10 },
            Audio = new AudioSettings { Encoder = "libopus", BitrateKbps = 160, SampleRate = 48000 },
            Streams = new StreamSettings { KeepSubtitles = true, KeepAttachments = true },
            BuiltIn = true,
        },
        new EncodePreset
        {
            Name = "Remux (copy)",
            Video = new VideoSettings { Encoder = "copy", Mode = RateControl.Copy },
            Audio = new AudioSettings { Encoder = "copy" },
            Streams = new StreamSettings { KeepSubtitles = true, KeepAttachments = true },
            OutputPattern = "{name}.remux",
            BuiltIn = true,
        },
    ];

    /// <summary>Built-ins, then the user's presets sorted by name. Unreadable files are skipped (and logged).</summary>
    public IReadOnlyList<EncodePreset> All()
    {
        var presets = new List<EncodePreset>(BuiltIn);
        if (!System.IO.Directory.Exists(Directory))
        {
            return presets;
        }

        foreach (string file in System.IO.Directory.EnumerateFiles(Directory, "*.json").Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                presets.Add(Read(File.ReadAllText(file)));
            }
            catch (JsonException ex)
            {
                VqLog.Warn("presets", $"skipping {file}", ex);
            }
        }

        return presets;
    }

    /// <summary>Saves a user preset (a built-in name gets " (copy)"), returning what was saved.</summary>
    public EncodePreset Save(EncodePreset preset)
    {
        string name = preset.Name.Trim();
        if (name.Length == 0 || BuiltIn.Any(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            name = (name.Length == 0 ? "Preset" : name) + " (copy)";
        }

        EncodePreset saved = preset with { Name = name, BuiltIn = false };
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllText(PathOf(name), Write(saved));
        return saved;
    }

    public bool Delete(string name)
    {
        string path = PathOf(name);
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    public static string Write(EncodePreset preset) => JsonSerializer.Serialize(preset, PresetJsonContext.Default.EncodePreset);

    public static EncodePreset Read(string json) =>
        JsonSerializer.Deserialize(json, PresetJsonContext.Default.EncodePreset) ?? throw new JsonException("empty preset");

    private string PathOf(string name) => Path.Combine(Directory, ReleaseName.Sanitize(name) + ".json");
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, UseStringEnumConverter = true)]
[JsonSerializable(typeof(EncodePreset))]
internal sealed partial class PresetJsonContext : JsonSerializerContext;
