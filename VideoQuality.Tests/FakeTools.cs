using System.Text.Json;
using System.Text.Json.Nodes;

namespace VideoQuality.Tests;

/// <summary>One scripted behaviour of a fake program (see VideoQuality.Tests.FakeTool/Program.cs).</summary>
public sealed record FakeRule
{
    public string[]? Match { get; init; }

    public string? Stdout { get; init; }

    public string? Stderr { get; init; }

    public string[]? StdoutLines { get; init; }

    public int LineDelayMs { get; init; }

    public string? WriteArgPattern { get; init; }

    public bool WriteLastArg { get; init; }

    public string? FileContent { get; init; }

    public int DelayMs { get; init; }

    public bool Hang { get; init; }

    public int Exit { get; init; }
}

/// <summary>A copy of FakeTool in its own folder, named like the program it pretends to be.</summary>
public sealed class FakeTool : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault,
    };

    private FakeTool(string directory, string path)
    {
        Directory = directory;
        Path = path;
    }

    /// <summary>The folder holding the fake (pass it where a tool folder is expected).</summary>
    public string Directory { get; }

    /// <summary>The fake executable.</summary>
    public string Path { get; }

    /// <summary>
    /// Creates <paramref name="name"/> (plus <c>.exe</c> on Windows) in a fresh folder, or in
    /// <paramref name="directory"/> when several fakes must sit side by side.
    /// </summary>
    public static FakeTool Create(string name, IEnumerable<FakeRule> rules, string? directory = null)
    {
        directory ??= System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vq-fake-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        string source = System.IO.Path.Combine(AppContext.BaseDirectory, "FakeTool");
        foreach (string file in System.IO.Directory.EnumerateFiles(source))
        {
            string target = System.IO.Path.Combine(directory, System.IO.Path.GetFileName(file));
            if (!File.Exists(target))
            {
                File.Copy(file, target);
            }
        }

        string exe = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        string path = System.IO.Path.Combine(directory, name + exe);
        File.Copy(System.IO.Path.Combine(source, "VideoQuality.Tests.FakeTool" + exe), path, overwrite: true);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, (UnixFileMode)0b111_101_101);
        }

        File.WriteAllText(path + ".fake.json", JsonSerializer.Serialize(rules, Json));
        return new FakeTool(directory, path);
    }

    public static FakeTool Create(string name, params FakeRule[] rules) => Create(name, rules, null);

    /// <summary>Argument lists of every run so far.</summary>
    public IReadOnlyList<string[]> Calls =>
        File.Exists(Path + ".calls.log")
            ? File.ReadAllLines(Path + ".calls.log").Select(line => JsonNode.Parse(line)!.AsArray().Select(a => a!.GetValue<string>()).ToArray()).ToList()
            : [];

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // A killed fake may still hold its file for a moment; the temp folder is cleaned later.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
