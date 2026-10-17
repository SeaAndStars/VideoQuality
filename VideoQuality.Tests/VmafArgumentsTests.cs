using VideoQuality.Core.Scoring;

namespace VideoQuality.Tests;

public class VmafArgumentsTests
{
    private static string FilterGraph(string logPath, string model = VmafModels.Default)
    {
        IReadOnlyList<string> args = VmafRunner.BuildArguments(
            new VmafOptions { Model = model, Threads = 4 },
            VmafExecutionBackend.Cpu,
            logPath,
            "ref.mkv",
            "dist.mp4");

        return args[args.ToList().IndexOf("-filter_complex") + 1];
    }

    [Fact]
    public void Windows_log_path_escapes_the_drive_colon_for_both_parser_levels()
    {
        string graph = FilterGraph(@"C:\Users\me\AppData\Local\Temp\vq.json");

        // Option level escapes ':' as "\:", graph level then escapes that backslash: "C\\:".
        Assert.Contains(@"log_path=C\\:/Users/me/AppData/Local/Temp/vq.json:log_fmt=json", graph);
    }

    [Fact]
    public void Graph_specials_in_a_model_path_are_escaped()
    {
        string graph = FilterGraph("/tmp/vq.json", "/models/a,b [v2];x'y.json");

        Assert.Contains(@"model=path=/models/a\,b \[v2\]\;x\\\'y.json", graph);
    }

    [Fact]
    public void Plain_linux_paths_are_unchanged()
    {
        Assert.Contains("log_path=/tmp/vq_vmaf_1.json:log_fmt=json", FilterGraph("/tmp/vq_vmaf_1.json"));
    }
}
