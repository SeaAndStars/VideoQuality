using System.Reflection;
using VideoQuality.Cli;
using VideoQuality.Core.Scoring;
using VideoQuality.Core.Tools;

namespace VideoQuality.Tests;

/// <summary>The vq commands, run in-process with scripted tools.</summary>
[Collection("ffmpeg")]
public sealed class CliTests : IDisposable
{
    private const string Filters = " ... libvmaf          VV->V      Calculate the VMAF\n ... libvmaf_cuda     VV->V      Calculate the VMAF (CUDA)\n";
    private const string GoodLog = """{"frames":[{"frameNum":0,"metrics":{"vmaf":90.5}},{"frameNum":1,"metrics":{"vmaf":91.5}}]}""";
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures", "fonts");
    private readonly string _work = Directory.CreateTempSubdirectory("vq-cli-").FullName;
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();

    public CliTests() => VmafRunner.ClearCapabilityCache();

    public void Dispose()
    {
        VmafRunner.ClearCapabilityCache();
        Directory.Delete(_work, true);
    }

    private string Ffmpeg() => Path.Combine(_work, "ffmpeg-" + Guid.NewGuid().ToString("N")[..6]);

    private static ScriptedRunner Tools(string filters = Filters, bool gpu = true, Func<ProcessRequest, int>? exit = null) =>
        new(request =>
        {
            if (request.FileName.Contains("nvidia-smi", StringComparison.Ordinal))
            {
                return ScriptedRunner.Ok(gpu ? "GPU 0: NVIDIA Test (UUID: x)\n" : "No devices were found\n");
            }

            if (request.Arguments.Contains("-filters"))
            {
                return ScriptedRunner.Ok(filters);
            }

            if (request.Arguments.Contains("-version"))
            {
                return ScriptedRunner.Ok("ffmpeg version 9.0 test\n");
            }

            ScriptedRunner.Emit(request, "frame=0", "progress=continue", "frame=1", "progress=continue", "frame=24", "progress=end");
            int code = exit?.Invoke(request) ?? 0;
            if (code == 0)
            {
                File.WriteAllText(ScriptedRunner.LogPath(request), GoodLog);
            }

            return new ProcessResult(code, string.Empty, code == 0 ? string.Empty : "first error line\nsecond");
        });

    private Cli.Cli Cli(IProcessRunner? runner = null, Func<string, string?>? environment = null, Func<string>? libav = null, string? log = "/logs/vq.log") =>
        new(_out, _err, runner ?? Tools(), environment ?? (_ => null), libav ?? (() => "libav test"), () => log);

    private string Video(string name)
    {
        string path = Path.Combine(_work, name);
        File.WriteAllText(path, "not media");
        return path;
    }

    [Theory]
    [InlineData(new string[0], 0)]
    [InlineData(new[] { "HELP" }, 0)]
    [InlineData(new[] { "-h" }, 0)]
    [InlineData(new[] { "--help" }, 0)]
    [InlineData(new[] { "bogus" }, 1)]
    public async Task Help_is_printed_for_unknown_commands(string[] args, int code)
    {
        Assert.Equal(code, await Cli().RunAsync(args));
        Assert.Contains("vq subset", _out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_entry_point_runs_the_cli()
    {
        MethodInfo main = typeof(Cli.Cli).Assembly.EntryPoint!;
        Assert.Equal(0, main.Invoke(null, [new[] { "--help" }]));
        Assert.NotNull(VideoQuality.Cli.Cli.Default());
    }

    [Fact]
    public async Task Probe_reports_capabilities_and_hints_when_vmaf_is_missing()
    {
        Assert.Equal(0, await Cli().RunAsync(["probe", "--ffmpeg", Ffmpeg()]));
        Assert.Contains("libvmaf_cuda: yes", _out.ToString(), StringComparison.Ordinal);
        Assert.Contains("log         : /logs/vq.log", _out.ToString(), StringComparison.Ordinal);

        Assert.Equal(0, await Cli(Tools(filters: string.Empty, gpu: false), libav: () => throw new InvalidOperationException("no libs"), log: null)
            .RunAsync(["probe", "--ffmpeg", Ffmpeg()]));
        string text = _out.ToString();
        Assert.Contains("No VMAF filter", text, StringComparison.Ordinal);
        Assert.Contains("unavailable: no libs", text, StringComparison.Ordinal);
        Assert.Contains("(disabled)", text, StringComparison.Ordinal);

        Assert.Equal(0, await Cli(Tools(filters: " ... libvmaf_cuda     VV->V      x\n")).RunAsync(["probe", "--ffmpeg", Ffmpeg()]));
        Assert.Equal(0, await Cli(Tools(filters: " ... libvmaf          VV->V      x\n")).RunAsync(["probe", "--ffmpeg", Ffmpeg()]));
    }

    [Fact]
    public async Task Score_needs_two_files()
    {
        Assert.Equal(1, await Cli().RunAsync(["score", "only-one.mkv", "--model", "x"]));
        Assert.Contains("usage: vq score", _err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Score_prints_stats_and_writes_reports()
    {
        string csv = Path.Combine(_work, "r.csv"), json = Path.Combine(_work, "r.json");
        int code = await Cli().RunAsync(["score", "--model", VmafModels.Standard, Video("r.mkv"), Video("d.mkv"), "--cpu", "--ffmpeg", Ffmpeg(), "--csv", csv, "--json", json]);

        Assert.Equal(0, code);
        string text = _out.ToString();
        Assert.Contains("scoring frame 24", text, StringComparison.Ordinal);
        Assert.Contains("mean    : 91.000", text, StringComparison.Ordinal);
        Assert.Contains("model   : VMAF (vmaf_v0.6.1)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("note", text, StringComparison.Ordinal);
        Assert.True(File.Exists(csv) && File.Exists(json));
    }

    [Fact]
    public async Task Score_notes_the_gpu_conversion_and_the_cpu_fallback()
    {
        string hevc10 = await TestMedia.Hevc10Async();
        Assert.Equal(0, await Cli().RunAsync(["score", hevc10, hevc10, "--gpu", "--ffmpeg", Ffmpeg()]));
        Assert.Contains("converted to 8-bit", _out.ToString(), StringComparison.Ordinal);

        ScriptedRunner failingCuda = Tools(exit: r => r.Arguments.Any(a => a.Contains("libvmaf_cuda", StringComparison.Ordinal)) ? 1 : 0);
        Assert.Equal(0, await Cli(failingCuda).RunAsync(["score", Video("r.mkv"), Video("d.mkv"), "--ffmpeg", Ffmpeg()]));
        Assert.Contains("CUDA failed, scored on CPU: ", _out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Score_failures_exit_with_2()
    {
        Assert.Equal(2, await Cli(Tools(filters: string.Empty)).RunAsync(["score", Video("r.mkv"), Video("d.mkv"), "--cpu", "--ffmpeg", Ffmpeg()]));
        Assert.Contains("error: ", _err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Frame_decodes_and_writes_a_ppm()
    {
        string ppm = Path.Combine(_work, "f.ppm");
        Assert.Equal(0, await Cli().RunAsync(["frame", await TestMedia.ClipAsync(), "x", "3", "--ppm", ppm]));
        Assert.Contains("frame 3 : 320x180", _out.ToString(), StringComparison.Ordinal);
        Assert.Equal((320 * 180 * 3) + "P6\n320 180\n255\n".Length, new FileInfo(ppm).Length);

        Assert.Equal(0, await Cli().RunAsync(["frame", await TestMedia.ClipAsync()]));
        Assert.Contains("frame 0 :", _out.ToString(), StringComparison.Ordinal);

        Assert.Equal(1, await Cli().RunAsync(["frame", "--ppm", ppm]));
        Assert.Equal(2, await Cli().RunAsync(["frame", Path.Combine(_work, "missing.mkv")]));
    }

    [Fact]
    public async Task Diag_prints_the_session()
    {
        Assert.Equal(0, await Cli(environment: name => name == "XDG_SESSION_TYPE" ? "wayland" : null).RunAsync(["diag", "--ffmpeg", Ffmpeg()]));
        Assert.Contains("session type : wayland", _out.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, await Cli().RunAsync(["diag", "--ffmpeg", Ffmpeg()]));
        Assert.Contains("session type : unknown", _out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Subset_reports_every_finding()
    {
        string fonts = Directory.CreateDirectory(Path.Combine(_work, "fonts")).FullName;
        File.Copy(Path.Combine(Fixtures, "DejaVuSans.ttf"), Path.Combine(fonts, "DejaVuSans.ttf"));
        File.Copy(Path.Combine(Fixtures, "VQTestCff.otf"), Path.Combine(fonts, "VQTestCff.otf"));
        string script = Path.Combine(_work, "ep.ass");
        File.WriteAllText(script, """
            [V4+ Styles]
            Format: Name, Fontname, Bold, Italic
            Style: Default,DejaVu Sans,0,0
            Style: Unused,Never Drawn,0,0

            [Events]
            Format: Style, Text
            Dialogue: Default,一二三四五六七八九十百千万{\i1}i{\i0\fnVQ Test CFF}é{\b1}b{\b0\fnNope}x
            """);

        Assert.Equal(2, await Cli().RunAsync(["subset", script]));
        string text = _out.ToString();
        Assert.Contains("MISSING   Nope", text, StringComparison.Ordinal);
        Assert.Contains("(faux italic)", text, StringComparison.Ordinal);
        Assert.Contains("(faux bold)", text, StringComparison.Ordinal);
        Assert.Contains(" …", text, StringComparison.Ordinal);
        Assert.Contains("U+00E9", text, StringComparison.Ordinal);
        Assert.Contains("unused    Never Drawn", text, StringComparison.Ordinal);
        Assert.Contains("wrote     ", text, StringComparison.Ordinal);

        File.WriteAllText(script, "[Events]\nFormat: Style, Text\nDialogue: x,{\\fnDejaVu Sans}ok");
        Assert.Equal(0, await Cli().RunAsync(["subset", script, "--fonts", fonts, "--output", Path.Combine(_work, "out2")]));
    }

    [Fact]
    public async Task Subset_usage_and_errors()
    {
        Assert.Equal(1, await Cli().RunAsync(["subset", "--fonts", "x"]));
        Assert.Contains("usage: vq subset", _err.ToString(), StringComparison.Ordinal);
        string script = Path.Combine(_work, "a.ass");
        File.WriteAllText(script, string.Empty);
        Assert.Equal(1, await Cli().RunAsync(["subset", script, "--output", _work]));
        Assert.Contains("error: ", _err.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "unknown error")]
    [InlineData("  ", "unknown error")]
    [InlineData("one", "one")]
    [InlineData("one\ntwo", "one")]
    public void First_line_of_an_error(string? text, string expected) =>
        Assert.Equal(expected, VideoQuality.Cli.Cli.FirstLine(text));

    [Fact]
    public void Options_and_positional_arguments()
    {
        string[] args = ["score", "--gpu", "a", "--model", "m", "b", "--json"];
        Assert.Equal(["a", "b"], VideoQuality.Cli.Cli.Positional(args));
        Assert.Equal("m", VideoQuality.Cli.Cli.GetOption(args, "--model"));
        Assert.Null(VideoQuality.Cli.Cli.GetOption(args, "--json"));
    }
}
