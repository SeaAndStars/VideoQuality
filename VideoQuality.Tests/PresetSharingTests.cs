using System.Net;
using System.Text;
using System.Text.Json;
using VideoQuality.Core.Encode;
using VideoQuality.Core.Media;

namespace VideoQuality.Tests;

/// <summary>Answers HTTP requests from a script, without a network.</summary>
public sealed class ScriptedHttp(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(answer(request));
    }
}

public sealed class PresetSharingTests : IDisposable
{
    private readonly string _work = Directory.CreateTempSubdirectory("vq-presets-").FullName;

    public void Dispose() => Directory.Delete(_work, true);

    [Fact]
    public void Files_hold_one_preset_an_array_or_a_bundle()
    {
        var a = new EncodePreset { Name = "A", BuiltIn = true };
        var b = new EncodePreset { Name = "B" };
        Assert.Equal(["A"], PresetStore.Parse(PresetStore.Write(a)).Select(p => p.Name));
        Assert.False(PresetStore.Parse(PresetStore.Write(a))[0].BuiltIn); // a shared file never makes a built-in
        Assert.Equal(["A", "B"], PresetStore.Parse($"[{PresetStore.Write(a)},{PresetStore.Write(b)}]").Select(p => p.Name));

        string bundle = PresetStore.WriteBundle([a, b]);
        Assert.Contains("\"format\": \"videoquality-presets\"", bundle, StringComparison.Ordinal);
        Assert.Equal(["A", "B"], PresetStore.Parse(bundle).Select(p => p.Name));
        Assert.All(PresetStore.Parse(bundle), p => Assert.False(p.BuiltIn));

        Assert.Throws<JsonException>(() => PresetStore.Parse("42"));
        Assert.ThrowsAny<JsonException>(() => PresetStore.Parse("not json"));
    }

    [Fact]
    public void Fields_a_file_leaves_out_keep_their_defaults()
    {
        // Hand-written files and presets saved by older versions omit fields; they must not turn
        // into nulls, zero quality or a variable frame rate.
        EncodePreset sparse = PresetStore.Read("""{"name":"Sparse","video":{"encoder":"libx264"}}""");
        Assert.Equal(PresetStore.Write(new EncodePreset { Name = "Sparse", Video = new VideoSettings { Encoder = "libx264" } }), PresetStore.Write(sparse));
        Assert.Equal(20, sparse.Video.Quality);
        Assert.True(sparse.Video.ConstantFrameRate);
        Assert.NotNull(sparse.Streams);
    }

    [Fact]
    public void Imports_never_overwrite_and_never_take_a_built_in_name()
    {
        var store = new PresetStore(_work);
        store.Save(new EncodePreset { Name = "Mine" });
        string builtIn = PresetStore.BuiltIn[0].Name;
        IReadOnlyList<EncodePreset> saved = store.Import(PresetStore.WriteBundle(
            [new EncodePreset { Name = "Mine" }, new EncodePreset { Name = "Mine" }, new EncodePreset { Name = builtIn }, new EncodePreset { Name = "  " }]));
        Assert.Equal(["Mine (2)", "Mine (3)", builtIn + " (2)", "Preset"], saved.Select(p => p.Name));
        Assert.Equal(5, store.User().Count);
        Assert.All(store.User(), p => Assert.False(p.BuiltIn));
    }

    [Theory]
    [InlineData("SeaAndStars/VideoQuality/presets@main", "SeaAndStars", "VideoQuality", "presets", "main")]
    [InlineData("  me/repo  ", "me", "repo", "", "main")]
    [InlineData("me/repo@dev", "me", "repo", "", "dev")]
    [InlineData("me/my.repo/a/b@feature/x", "me", "my.repo", "a/b", "feature/x")]
    [InlineData("https://github.com/me/repo/tree/dev/presets/anime", "me", "repo", "presets/anime", "dev")]
    [InlineData("https://github.com/me/repo/tree/main", "me", "repo", "", "main")]
    public void Sources_name_a_github_folder(string text, string owner, string repo, string folder, string branch)
    {
        PresetSource source = PresetSource.Parse(text);
        Assert.Equal((owner, repo, folder, branch, (string?)null), (source.Owner, source.Repository, source.Folder, source.Branch, source.FileUrl));
        Assert.StartsWith($"https://api.github.com/repos/{owner}/{repo}/contents/", source.ListUrl, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://raw.githubusercontent.com/me/repo/main/presets/a.json")]
    [InlineData("https://github.com/me/repo/blob/main/a.json")]
    [InlineData("http://example.org/presets.json")]
    public void Other_links_are_single_files(string url) =>
        Assert.Equal(url, PresetSource.Parse(url).FileUrl);

    [Theory]
    [InlineData("")]
    [InlineData("just-a-name")]
    [InlineData("ftp://example.org/a.json")]
    public void Anything_else_is_not_a_source(string text) =>
        Assert.Throws<FormatException>(() => PresetSource.Parse(text));

    [Fact]
    public async Task A_github_folder_lists_its_preset_files()
    {
        string listing = """
            [
              {"name":"b.json","type":"file","download_url":"https://raw/b.json"},
              {"name":"a.JSON","type":"file","download_url":"https://raw/a.json"},
              {"name":"README.md","type":"file","download_url":"https://raw/README.md"},
              {"name":"old","type":"dir","download_url":null},
              {"name":"broken.json","type":"file","download_url":"https://raw/broken.json"}
            ]
            """;
        var http = new ScriptedHttp(request => request.RequestUri!.ToString() switch
        {
            var u when u.StartsWith("https://api.github.com/", StringComparison.Ordinal) => ScriptedHttp.Json(listing),
            "https://raw/a.json" => ScriptedHttp.Json(PresetStore.Write(new EncodePreset { Name = "A" })),
            "https://raw/b.json" => ScriptedHttp.Json(PresetStore.WriteBundle([new EncodePreset { Name = "B1" }, new EncodePreset { Name = "B2" }])),
            _ => ScriptedHttp.Json("{ broken"),
        });

        IReadOnlyList<OnlinePreset> found = await new OnlinePresets(new HttpClient(http)).ListAsync("me/repo/presets@dev", TestContext.Current.CancellationToken);
        Assert.Equal(["A", "B1", "B2"], found.Select(p => p.Preset.Name));
        Assert.Equal(("a.JSON", "https://raw/a.json"), (found[0].FileName, found[0].Url));
        Assert.Equal("https://api.github.com/repos/me/repo/contents/presets?ref=dev", http.Requests[0].RequestUri!.ToString());
        Assert.Contains("VideoQuality", http.Requests[0].Headers.UserAgent.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_file_can_be_the_source()
    {
        var http = new ScriptedHttp(_ => ScriptedHttp.Json(PresetStore.Write(new EncodePreset { Name = "Solo" })));
        IReadOnlyList<OnlinePreset> found = await new OnlinePresets(new HttpClient(http)).ListAsync("https://example.org/p/solo.json", TestContext.Current.CancellationToken);
        Assert.Equal(("solo.json", "Solo"), (found.Single().FileName, found.Single().Preset.Name));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "Not found")]
    [InlineData(HttpStatusCode.Forbidden, "rate limit")]
    [InlineData(HttpStatusCode.TooManyRequests, "rate limit")]
    [InlineData(HttpStatusCode.InternalServerError, "500")]
    public async Task Server_errors_are_explained(HttpStatusCode status, string expected)
    {
        var http = new ScriptedHttp(_ => ScriptedHttp.Json("{}", status));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new OnlinePresets(new HttpClient(http)).ListAsync("me/repo", TestContext.Current.CancellationToken));
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Offline_and_non_folders_are_explained()
    {
        var offline = new ScriptedHttp(_ => throw new HttpRequestException("no route to host"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new OnlinePresets(new HttpClient(offline)).ListAsync("me/repo", TestContext.Current.CancellationToken));
        Assert.Contains("no route to host", error.Message, StringComparison.Ordinal);

        var file = new ScriptedHttp(_ => ScriptedHttp.Json("""{"name":"a.json","type":"file"}"""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new OnlinePresets(new HttpClient(file)).ListAsync("me/repo/a.json", TestContext.Current.CancellationToken));
    }

    /// <summary>The community presets in the repository's presets/ folder are what users download.</summary>
    [Fact]
    public void The_shipped_community_presets_parse_and_build()
    {
        string folder = Path.Combine(RepositoryRoot(), "presets");
        string[] files = Directory.GetFiles(folder, "*.json");
        Assert.NotEmpty(files);
        var source = new MediaInfo
        {
            Path = "/in.mkv",
            Duration = TimeSpan.FromMinutes(24),
            Streams = [new MediaStream { Kind = StreamKind.Video, Codec = "h264", Width = 1920, Height = 1080 }, new MediaStream { Index = 1, Kind = StreamKind.Audio, Codec = "aac", SampleRate = 48000, Channels = 2 }],
        };
        foreach (string file in files)
        {
            foreach (EncodePreset preset in PresetStore.Parse(File.ReadAllText(file)))
            {
                Assert.False(string.IsNullOrWhiteSpace(preset.Description), file);
                Assert.NotEmpty(FfmpegCommandBuilder.Build(preset, "/in.mkv", source, "/out/o." + preset.Container, "/tmp").Passes);
            }
        }
    }

    private static string RepositoryRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "VideoQuality.slnx")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        return directory!;
    }
}
