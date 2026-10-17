using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VideoQuality.Core.Fonts;
using VideoQuality.Core.Ingest;
using VideoQuality.Core.Mkv;
using VideoQuality.Core.Models;
using VideoQuality.Core.Scoring;

namespace VideoQuality.App.ViewModels;

public enum StepState
{
    Pending,
    Running,
    Done,
    Failed,
    Skipped,
}

/// <summary>One stage of the release pipeline, shown as a row with a status icon.</summary>
public partial class PipelineStep : ObservableObject
{
    public required string Title { get; init; }

    public required string Description { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPending), nameof(IsRunning), nameof(IsDone), nameof(IsFailed), nameof(IsSkipped))]
    public partial StepState State { get; set; }

    [ObservableProperty]
    public partial string Detail { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double Progress { get; set; }

    public bool IsPending => State == StepState.Pending;

    public bool IsRunning => State == StepState.Running;

    public bool IsDone => State == StepState.Done;

    public bool IsFailed => State == StepState.Failed;

    public bool IsSkipped => State == StepState.Skipped;
}

/// <summary>
/// Release pipeline for a finished episode: subset fonts → mux → verify the file → VMAF-check it
/// against the reference. Each stage reuses the same Core code as its own page.
/// </summary>
public partial class PipelineViewModel : ObservableObject
{
    public PipelineViewModel(SettingsViewModel settings, MainViewModel compare)
    {
        Settings = settings;
        Compare = compare;
        Scripts.CollectionChanged += (_, _) => RunCommand.NotifyCanExecuteChanged();
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.MkvToolNix))
            {
                RunCommand.NotifyCanExecuteChanged();
            }
        };
    }

    public SettingsViewModel Settings { get; }

    public MainViewModel Compare { get; }

    public ObservableCollection<string> Scripts { get; } = [];

    public PipelineStep SubsetStep { get; } = new() { Title = "Subset fonts", Description = "Collect the characters each font draws, subset and rename." };

    public PipelineStep MuxStep { get; } = new() { Title = "Mux", Description = "Encode + scripts (with languages) + subset fonts → MKV." };

    public PipelineStep VerifyStep { get; } = new() { Title = "Verify", Description = "Re-read the MKV: video, one subtitle track per script, every font attached." };

    public PipelineStep VmafStep { get; } = new() { Title = "VMAF check", Description = "Score the muxed file against the reference (optional)." };

    public IReadOnlyList<PipelineStep> Steps => [SubsetStep, MuxStep, VerifyStep, VmafStep];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial string Video { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Reference { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FontDirectory { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Output { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(OpenInCompareCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = "Drop the encoded episode, its scripts and the font folder.";

    public event EventHandler<ToastRequest>? ToastRequested;

    /// <summary>Raised by "Open in Compare" so the shell can switch pages.</summary>
    public event EventHandler? CompareRequested;

    /// <summary>Sorts dropped files: video (first = encode, second = reference), scripts, font folder.</summary>
    public void AddPaths(IEnumerable<string?> paths)
    {
        foreach (string path in paths.OfType<string>())
        {
            string extension = Path.GetExtension(path).ToLowerInvariant();
            if (Directory.Exists(path))
            {
                FontDirectory = path;
            }
            else if (extension is ".ass" or ".ssa")
            {
                if (!Scripts.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    Scripts.Add(path);
                }

                if (FontDirectory.Length == 0)
                {
                    string folder = Path.GetDirectoryName(path)!;
                    FontDirectory = new[] { "Fonts", "fonts", "字体" }.Select(n => Path.Combine(folder, n)).FirstOrDefault(Directory.Exists) ?? string.Empty;
                }
            }
            else if (DropRouter.IsVideo(path))
            {
                if (Video.Length == 0)
                {
                    Video = path;
                    string stem = Path.GetFileNameWithoutExtension(path);
                    Output = Path.Combine(Path.GetDirectoryName(path)!, stem + (extension == ".mkv" ? ".release.mkv" : ".mkv"));
                    Title = Title.Length == 0 ? stem : Title;
                }
                else if (Reference.Length == 0)
                {
                    Reference = path;
                }
            }
        }
    }

    [RelayCommand]
    private void RemoveScript(string path) => Scripts.Remove(path);

    [RelayCommand]
    private void Clear()
    {
        Scripts.Clear();
        Video = Reference = FontDirectory = Output = Title = string.Empty;
        foreach (PipelineStep step in Steps)
        {
            step.State = StepState.Pending;
            step.Detail = string.Empty;
            step.Progress = 0;
        }

        Status = "Drop the encoded episode, its scripts and the font folder.";
    }

    private bool CanRun() => !IsBusy && Video.Length > 0 && Settings.HasMkvToolNix;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        if (Settings.MkvToolNix is not { } tools)
        {
            return;
        }

        IsBusy = true;
        foreach (PipelineStep step in Steps)
        {
            step.State = StepState.Pending;
            step.Detail = string.Empty;
            step.Progress = 0;
        }

        try
        {
            string workFolder = Path.Combine(Path.GetDirectoryName(Output)!, Path.GetFileNameWithoutExtension(Output) + "_subset");
            FontSubsetReport? subset = null;

            // 1. Subset
            if (Scripts.Count == 0)
            {
                Skip(SubsetStep, "No scripts: nothing to subset.");
            }
            else
            {
                Begin(SubsetStep);
                var options = new FontSubsetOptions
                {
                    Scripts = Scripts.ToList(),
                    FontDirectories = FontDirectory.Length > 0 ? [FontDirectory] : [Path.GetDirectoryName(Scripts[0])!],
                    OutputDirectory = workFolder,
                };
                subset = await Task.Run(() => FontSubsetter.Run(options, new Progress<string>(m => SubsetStep.Detail = m)));
                if (!subset.Succeeded)
                {
                    Fail(SubsetStep, "Missing font(s): " + string.Join(", ", subset.MissingFonts));
                    return;
                }

                Done(SubsetStep, $"{subset.Fonts.Count} font(s), {FontSubsetViewModel.Size(subset.Fonts.Sum(f => f.SourceBytes))} → " +
                                 $"{FontSubsetViewModel.Size(subset.Fonts.Sum(f => f.SubsetBytes))}");
            }

            // 2. Mux
            Begin(MuxStep);
            var inputs = new List<MuxInput> { new(Video) };
            var identified = new Dictionary<string, MkvFileInfo>();
            bool firstSubtitle = true;
            foreach (string script in subset?.ScriptsWritten ?? Scripts.ToList())
            {
                (string Language, string Name)? guess = SubtitleNaming.Guess(script);
                inputs.Add(new MuxInput(script, [new MuxTrack(0, guess?.Language, guess?.Name, Default: firstSubtitle)]));
                identified[script] = await tools.IdentifyAsync(script);
                firstSubtitle = false;
            }

            List<string> fonts = subset?.Fonts.Select(f => f.OutputPath).ToList() ?? [];
            var plan = new MuxPlan(Output, inputs, fonts, Title: Title.Length > 0 ? Title : null);
            MkvRunResult mux = await tools.MuxAsync(MkvToolNix.BuildMuxArguments(plan, identified), new Progress<double>(p => MuxStep.Progress = p), default);
            if (!mux.Succeeded)
            {
                Fail(MuxStep, string.Join("; ", mux.Errors));
                return;
            }

            Done(MuxStep, $"{Path.GetFileName(Output)}" + (mux.Warnings.Count > 0 ? $" ({mux.Warnings.Count} warning(s): {mux.Warnings[0]})" : string.Empty));

            // 3. Verify
            Begin(VerifyStep);
            MkvFileInfo written = await tools.IdentifyAsync(Output);
            var problems = new List<string>();
            if (!written.Tracks.Any(t => t.Type == MkvTrackType.Video))
            {
                problems.Add("no video track");
            }

            // At least one per script; subtitle tracks already in the video come on top.
            int expectedSubtitles = subset?.ScriptsWritten.Count ?? Scripts.Count;
            int subtitles = written.Tracks.Count(t => t.Type == MkvTrackType.Subtitles);
            if (subtitles < expectedSubtitles)
            {
                problems.Add($"{subtitles} subtitle track(s), expected {expectedSubtitles}");
            }

            var attached = written.Attachments.Select(a => a.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missingFonts = fonts.Select(Path.GetFileName).Where(f => !attached.Contains(f!)).ToList();
            if (missingFonts.Count > 0)
            {
                problems.Add("fonts not attached: " + string.Join(", ", missingFonts));
            }

            if (problems.Count > 0)
            {
                Fail(VerifyStep, string.Join("; ", problems));
                return;
            }

            Done(VerifyStep, $"{written.Tracks.Count} track(s), {written.Attachments.Count} attachment(s), " +
                             $"{TimeSpan.FromSeconds(written.DurationSeconds):hh\\:mm\\:ss}");

            // 4. VMAF
            if (Reference.Length == 0)
            {
                Skip(VmafStep, "No reference: drop the source video too to score the release.");
            }
            else
            {
                Begin(VmafStep);
                string ffmpeg = await VmafRunner.ResolveFfmpegAsync(Compare.FfmpegPath);
                var vmafOptions = new VmafOptions { FfmpegPath = ffmpeg, Backend = Compare.SelectedBackend, Model = Compare.SelectedModel };
                VmafReport report = await VmafRunner.RunAsync(Reference, Output, vmafOptions,
                    new Progress<VmafProgress>(p => VmafStep.Detail = p.FrameNumber is int f ? $"frame {f}" : VmafStep.Detail));
                Done(VmafStep, string.Format(CultureInfo.InvariantCulture,
                    "{0} mean {1:F2} · p1 {2:F2} · min {3:F2} · {4}",
                    VmafModels.ShortName(report.Model), report.Stats.Mean, report.Stats.P1, report.Stats.Min, report.Backend.ToDisplayName()));
            }

            Status = $"Release ready: {Output}";
            ToastRequested?.Invoke(this, new ToastRequest(ToastKind.Success, "Pipeline finished: " + Path.GetFileName(Output)));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            PipelineStep? running = Steps.FirstOrDefault(s => s.IsRunning);
            if (running is not null)
            {
                Fail(running, ex.Message);
            }

            Status = "Pipeline stopped: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanOpenInCompare() => !IsBusy && Reference.Length > 0 && File.Exists(Output);

    [RelayCommand(CanExecute = nameof(CanOpenInCompare))]
    private async Task OpenInCompareAsync()
    {
        CompareRequested?.Invoke(this, EventArgs.Empty);
        await Compare.HandleDropAsync([Reference, Output], DropZone.Auto);
    }

    private void Begin(PipelineStep step)
    {
        step.State = StepState.Running;
        step.Progress = 0;
        Status = step.Title + "…";
    }

    private void Done(PipelineStep step, string detail)
    {
        step.State = StepState.Done;
        step.Progress = 1;
        step.Detail = detail;
    }

    private void Fail(PipelineStep step, string detail)
    {
        step.State = StepState.Failed;
        step.Detail = detail;
        Status = $"{step.Title} failed.";
        ToastRequested?.Invoke(this, new ToastRequest(ToastKind.Error, $"{step.Title} failed: {detail}"));
    }

    private static void Skip(PipelineStep step, string detail)
    {
        step.State = StepState.Skipped;
        step.Detail = detail;
    }
}
