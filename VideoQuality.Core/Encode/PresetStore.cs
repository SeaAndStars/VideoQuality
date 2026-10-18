using System.Text.Json;
using System.Text.Json.Serialization;
using VideoQuality.Core.Diagnostics;
using VideoQuality.Core.Localization;
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
            Name = "NVENC HEVC UHQ",
            Description = Loc.T("presets.nvencUhq.description"),
            OutputPattern = "{name}.nvenc",
            Video = new VideoSettings { Encoder = "hevc_nvenc", Quality = NvencUhq.Quality, Preset = "p7", Tune = "uhq", BitDepth = 10 },
            Audio = new AudioSettings { Encoder = "aac", BitrateKbps = 256, SampleRate = 44100, Channels = 2 },
            Streams = new StreamSettings { KeepSubtitles = true, KeepAttachments = true },
            Custom = new CustomArguments { Video = NvencUhq.Arguments },
            BuiltIn = true,
        },
        .. GreenTeaSpec.Presets.Take(2).Select(p => NvencUhq.Apply(p, NvencUhq.Quality) with
        {
            Name = p.Name + " · NVENC UHQ",
            Description = Loc.T("presets.nvencUhq.greenTea"),
        }),
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

    /// <summary>
    /// Saves presets from a file someone shared (one preset, an array, or a bundle). A name that is
    /// already taken gets " (2)", " (3)", …, so nothing of the user's is overwritten.
    /// </summary>
    public IReadOnlyList<EncodePreset> Import(string json) => Import(Parse(json));

    /// <inheritdoc cref="Import(string)"/>
    public IReadOnlyList<EncodePreset> Import(IEnumerable<EncodePreset> presets)
    {
        var saved = new List<EncodePreset>();
        foreach (EncodePreset preset in presets)
        {
            saved.Add(Save(preset with { Name = FreeName(preset.Name.Trim()) }));
        }

        return saved;
    }

    /// <summary>The user's own presets (not the built-ins).</summary>
    public IReadOnlyList<EncodePreset> User() => [.. All().Where(p => !p.BuiltIn)];

    private string FreeName(string name)
    {
        string baseName = name.Length == 0 ? "Preset" : name;
        string candidate = baseName;
        for (int n = 2; Taken(candidate); n++)
        {
            candidate = $"{baseName} ({n})";
        }

        return candidate;
    }

    private bool Taken(string name) =>
        File.Exists(PathOf(name)) || BuiltIn.Any(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));

    public static string Write(EncodePreset preset) => JsonSerializer.Serialize(preset, PresetJsonContext.Default.EncodePreset);

    public static EncodePreset Read(string json) =>
        JsonSerializer.Deserialize(json, PresetJsonContext.Default.EncodePreset) ?? throw new JsonException("empty preset");

    /// <summary>Several presets in one shareable file.</summary>
    public static string WriteBundle(IEnumerable<EncodePreset> presets) => JsonSerializer.Serialize(
        new PresetBundle { Presets = [.. presets.Select(p => p with { BuiltIn = false })] }, PresetJsonContext.Default.PresetBundle);

    /// <summary>
    /// The presets in a shared file: a bundle (<c>{"format": "videoquality-presets", "presets": […]}</c>),
    /// a JSON array of presets, or a single preset. Built-in flags in the file are ignored.
    /// </summary>
    public static IReadOnlyList<EncodePreset> Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        IReadOnlyList<EncodePreset> presets = root.ValueKind switch
        {
            JsonValueKind.Array => root.Deserialize(PresetJsonContext.Default.ListEncodePreset)!,
            JsonValueKind.Object when root.TryGetProperty("presets", out _) => root.Deserialize(PresetJsonContext.Default.PresetBundle)!.Presets,
            JsonValueKind.Object => [root.Deserialize(PresetJsonContext.Default.EncodePreset)!],
            _ => throw new JsonException(Localization.Loc.T("presets.error.notPresets")),
        };

        return [.. presets.Select(p => p with { BuiltIn = false })];
    }

    private string PathOf(string name) => Path.Combine(Directory, ReleaseName.Sanitize(name) + ".json");
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, UseStringEnumConverter = true)]
[JsonSerializable(typeof(EncodePreset))]
[JsonSerializable(typeof(List<EncodePreset>))]
[JsonSerializable(typeof(PresetBundle))]
internal sealed partial class PresetJsonContext : JsonSerializerContext;

/// <summary>The file format for sharing several presets.</summary>
public sealed record PresetBundle
{
    public string Format { get; init; } = "videoquality-presets";

    public int Version { get; init; } = 1;

    public List<EncodePreset> Presets { get; init; } = [];
}
