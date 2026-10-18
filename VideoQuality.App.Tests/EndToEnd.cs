using Avalonia.Headless.XUnit;
using VideoQuality.App.ViewModels;
using VideoQuality.Core.Encode;
using VideoQuality.Core.Scoring;

namespace VideoQuality.App.Tests;

/// <summary>
/// The whole pipeline on a real episode, as the app runs it: <c>VQ_E2E_SOURCE=/path/ep.mkv VQ_E2E_OUT=/dir
/// [VQ_E2E_ENCODER=nvenc|x265] dotnet test --filter EndToEnd</c>. Skipped otherwise. Prints each step's result.
/// </summary>
public class EndToEnd(ITestOutputHelper output)
{
    [AvaloniaFact]
    public async Task A_release_from_a_real_episode()
    {
        string? source = Environment.GetEnvironmentVariable("VQ_E2E_SOURCE");
        string? folder = Environment.GetEnvironmentVariable("VQ_E2E_OUT");
        Assert.SkipWhen(string.IsNullOrEmpty(source) || string.IsNullOrEmpty(folder), "set VQ_E2E_SOURCE and VQ_E2E_OUT to run a real release");

        SettingsViewModel settings = await Pages.SettingsAsync();
        var compare = new MainViewModel { FfmpegPath = await VmafRunner.ResolveFfmpegAsync(), SelectedBackend = VmafBackend.Auto };
        var queue = new EncodeQueue(settings.Services.Processes, () => settings.Encoder!.Path);
        var vm = new PipelineViewModel(settings, compare, queue, new NamingViewModel()) { OutputDirectory = folder! };
        vm.SetVideo(source!);
        await vm.Inference;
        if (Environment.GetEnvironmentVariable("VQ_E2E_ENCODER") != "x265")
        {
            vm.Encoder = PipelineEncoder.NvencUhq;
        }

        vm.VerifyStrict = false; // list every problem instead of stopping at the first file
        output.WriteLine($"inferred: {vm.Profile}, '{vm.Naming.Title}' ep {vm.Naming.Episode}, {vm.Scripts.Count} scripts, fonts '{vm.FontDirectory}', MP4s {string.Join("/", vm.Burns.Select(b => b.Language))}");
        await vm.RunCommand.ExecuteAsync(null);
        foreach (PipelineStep step in vm.Steps)
        {
            output.WriteLine($"{step.Title}: {step.State} — {step.Detail}");
        }

        output.WriteLine("written: " + string.Join(" | ", vm.Outputs.Select(Path.GetFileName)));
        Assert.DoesNotContain(vm.Steps, s => s.IsFailed);
    }
}
