using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VideoQuality.Core.Chapters;
using VideoQuality.Core.Encode;
using VideoQuality.Core.Fonts;
using VideoQuality.Core.Ingest;
using VideoQuality.Core.Localization;
using VideoQuality.Core.Media;
using VideoQuality.Core.Mkv;
using VideoQuality.Core.Models;
using VideoQuality.Core.Release;
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
/// The group's release flow in one run: encode the source (x265 10-bit CRF), subset the scripts'
/// fonts, mux the 内封 MKV, burn the 内嵌 MP4s, check every file against the rules, and score the
/// encode against the source with VMAF. File names come from the Naming page.
/// </summary>
public partial class PipelineViewModel : ObservableObject
{
    private readonly Func<string, MediaInfo> _read;

    public PipelineViewModel(SettingsViewModel settings, MainViewModel compare, EncodeQueue queue, NamingViewModel naming, Func<string, MediaInfo>? read = null)
    {
        Settings = settings;
        Compare = compare;
        Queue = queue;
        Naming = naming;
        _read = read ?? MediaInfoReader.Read;
        Scripts.CollectionChanged += (_, _) => RunCommand.NotifyCanExecuteChanged();
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SettingsViewModel.MkvToolNix) or nameof(SettingsViewModel.Encoder))
            {
                RunCommand.NotifyCanExecuteChanged();
            }
        };
        naming.PropertyChanged += (_, _) => OnPropertyChanged(nameof(OutputNames));
    }

    public SettingsViewModel Settings { get; }

    public MainViewModel Compare { get; }

    public EncodeQueue Queue { get; }

    public NamingViewModel Naming { get; }

    public ObservableCollection<string> Scripts { get; } = [];

    public IReadOnlyList<ReleaseProfile> Profiles { get; } = Enum.GetValues<ReleaseProfile>();

    public PipelineStep EncodeStep { get; } = new() { Title = Loc.T("pipeline.step.encode"), Description = Loc.T("pipeline.step.encode.description") };

    public PipelineStep SubsetStep { get; } = new() { Title = Loc.T("pipeline.step.subset"), Description = Loc.T("pipeline.step.subset.description") };

    public PipelineStep MuxStep { get; } = new() { Title = Loc.T("pipeline.step.mux"), Description = Loc.T("pipeline.step.mux.description") };

    public PipelineStep BurnStep { get; } = new() { Title = Loc.T("pipeline.step.burn"), Description = Loc.T("pipeline.step.burn.description") };

    public PipelineStep VerifyStep { get; } = new() { Title = Loc.T("pipeline.step.verify"), Description = Loc.T("pipeline.step.verify.description") };

    public PipelineStep VmafStep { get; } = new() { Title = Loc.T("pipeline.step.vmaf"), Description = Loc.T("pipeline.step.vmaf.description") };

    public IReadOnlyList<PipelineStep> Steps => [EncodeStep, SubsetStep, MuxStep, BurnStep, VerifyStep, VmafStep];

    /// <summary>The source (raw) video, or an encode when <see cref="AlreadyEncoded"/>.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial string Video { get; set; } = string.Empty;

    /// <summary>The video is already encoded: skip encoding (the MP4s are burned from it).</summary>
    [ObservableProperty]
    public partial bool AlreadyEncoded { get; set; }

    /// <summary>VMAF reference when the video is already encoded (otherwise the source itself).</summary>
    [ObservableProperty]
    public partial string Reference { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FontDirectory { get; set; } = string.Empty;

    /// <summary>Chapter file (.xml/.txt); empty uses the source's own chapters.</summary>
    [ObservableProperty]
    public partial string ChaptersFile { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string OutputDirectory { get; set; } = string.Empty;

    [ObservableProperty]
    public partial ReleaseProfile Profile { get; set; }

    [ObservableProperty]
    public partial decimal Crf { get; set; } = 20;

    [ObservableProperty]
    public partial bool MakeBurned { get; set; } = true;

    [ObservableProperty]
    public partial bool RunVmaf { get; set; } = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(OpenInCompareCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = Loc.T("pipeline.status.start");

    /// <summary>The encode the VMAF step scores (kept so "Open in Compare" can show it).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenInCompareCommand))]
    public partial string EncodedFile { get; set; } = string.Empty;

    /// <summary>The release file names (from the Naming page).</summary>
    public IReadOnlyList<string> OutputNames => Naming.Set().Select(n => n.FileName).ToList();

    public event EventHandler<ToastRequest>? ToastRequested;

    /// <summary>Raised by "Open in Compare" so the shell can switch pages.</summary>
    public event EventHandler? CompareRequested;

    partial void OnProfileChanged(ReleaseProfile value)
    {
        // The rules: BD releases are the 内封 MKV only; web releases add the two 内嵌 MP4s.
        bool bd = GreenTeaSpec.IsBd(value);
        MakeBurned = !bd;
        Crf = bd ? 18 : 20;
        Naming.Source = bd ? "BDRip" : "WebRip";
        Naming.AudioCodec = bd ? "FLAC" : "AAC";
        if (GreenTeaSpec.IsMovie(value))
        {
            Naming.Kind = EpisodeKind.Movie;
        }
        else if (Naming.Kind == EpisodeKind.Movie)
        {
            Naming.Kind = EpisodeKind.Episode;
        }
    }

    /// <summary>Sorts dropped files: videos (first = source, second = reference), scripts, chapters, font folder.</summary>
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
            else if (extension is ".xml" or ".txt")
            {
                ChaptersFile = path;
            }
            else if (DropRouter.IsVideo(path))
            {
                if (Video.Length == 0)
                {
                    SetVideo(path);
                }
                else if (Reference.Length == 0)
                {
                    Reference = path;
                }
            }
        }
    }

    /// <summary>Uses a video as the source: output folder beside it, release fields from it.</summary>
    public void SetVideo(string path)
    {
        Video = path;
        if (OutputDirectory.Length == 0)
        {
            OutputDirectory = Path.Combine(Path.GetDirectoryName(path)!, "release");
        }

        _ = Naming.FillFromAsync(path);
    }

    [RelayCommand]
    private void RemoveScript(string path) => Scripts.Remove(path);

    [RelayCommand]
    private void Clear()
    {
        Scripts.Clear();
        Video = Reference = FontDirectory = ChaptersFile = OutputDirectory = EncodedFile = string.Empty;
        AlreadyEncoded = false;
        ResetSteps();
        Status = Loc.T("pipeline.status.start");
    }

    private bool CanRun() => !IsBusy && Video.Length > 0 && Settings.HasMkvToolNix && Settings.HasEncoder;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        MkvToolNix tools = Settings.MkvToolNix!;
        IsBusy = true;
        ResetSteps();
        string work = Path.Combine(OutputDirectory, "work");
        IReadOnlyList<ReleaseName> names = Naming.Set();
        var outputs = new List<(string Path, SubtitleDelivery Delivery)>();
        try
        {
            Directory.CreateDirectory(work);
            MediaInfo source = await Task.Run(() => _read(Video));
            string chapters = ChapterFiles(source, work, out string ffmetadata);

            // 1. Encode (video + audio only; subtitles, fonts and chapters are muxed afterwards).
            string encoded = Video;
            if (AlreadyEncoded)
            {
                Skip(EncodeStep, Loc.T("pipeline.encode.skipped"));
            }
            else
            {
                Begin(EncodeStep);
                EncodePreset preset = GreenTeaSpec.Preset(Profile, SubtitleDelivery.Embedded);
                preset = preset with
                {
                    Video = preset.Video with { Quality = (double)Crf },
                    Streams = new StreamSettings { KeepChapters = false, KeepMetadata = false },
                };
                encoded = Path.Combine(work, "encoded.mkv");
                EncodeJob job = await RunEncodeAsync(EncodeStep,
                    new EncodeJob(Video, FfmpegCommandBuilder.Build(preset, Video, source, encoded, work), Loc.T("pipeline.job.encode", names[0].FileName)));
                if (job.State != EncodeJobState.Done)
                {
                    Fail(EncodeStep, job.Error ?? Loc.T("encode.row.cancelled"));
                    return;
                }

                Done(EncodeStep, MediaInfo.FormatSize(job.WrittenSize));
            }

            EncodedFile = encoded;

            // 2. Subset
            FontSubsetReport? subset = null;
            if (Scripts.Count == 0)
            {
                Skip(SubsetStep, Loc.T("pipeline.subset.noScripts"));
            }
            else
            {
                Begin(SubsetStep);
                var options = new FontSubsetOptions
                {
                    Scripts = Scripts.ToList(),
                    FontDirectories = FontDirectory.Length > 0 ? [FontDirectory] : [Path.GetDirectoryName(Scripts[0])!],
                    OutputDirectory = Path.Combine(work, "subset"),
                };
                subset = await Task.Run(() => FontSubsetter.Run(options, new Progress<string>(m => SubsetStep.Detail = m)));
                if (!subset.Succeeded)
                {
                    Fail(SubsetStep, Loc.T("pipeline.subset.missingFonts", string.Join(", ", subset.MissingFonts)));
                    return;
                }

                Done(SubsetStep, Loc.T("pipeline.subset.done", subset.Fonts.Count, FontSubsetViewModel.Size(subset.Fonts.Sum(f => f.SourceBytes)),
                    FontSubsetViewModel.Size(subset.Fonts.Sum(f => f.SubsetBytes))));
            }

            // 3. Mux the 内封 MKV.
            Begin(MuxStep);
            string mkv = Path.Combine(OutputDirectory, names[0].FileName);
            MkvFileInfo encodedInfo = await tools.IdentifyAsync(encoded);
            var identified = new Dictionary<string, MkvFileInfo> { [encoded] = encodedInfo };
            bool greenTea = Settings.TrackNaming == TrackNamingProfile.GreenTea;
            var inputs = new List<MuxInput>
            {
                // Only the encode's video and audio; audio is the Japanese original, unnamed under the rules.
                new(encoded, [.. encodedInfo.Tracks
                    .Where(t => t.Type is MkvTrackType.Video or MkvTrackType.Audio)
                    .Select(t => t.Type == MkvTrackType.Audio && greenTea ? new MuxTrack(t.Id, "ja", string.Empty) : new MuxTrack(t.Id))],
                    KeepAttachments: false, KeepChapters: chapters.Length == 0),
            };
            bool firstSubtitle = true;
            foreach (string script in subset?.ScriptsWritten ?? Scripts.ToList())
            {
                (string Language, string Name)? guess = SubtitleNaming.Guess(script, Settings.TrackNaming);
                inputs.Add(new MuxInput(script, [new MuxTrack(0, guess?.Language, guess?.Name, Default: firstSubtitle)]));
                identified[script] = await tools.IdentifyAsync(script);
                firstSubtitle = false;
            }

            List<string> fonts = subset?.Fonts.Select(f => f.OutputPath).ToList() ?? [];
            var plan = new MuxPlan(mkv, inputs, fonts, chapters.Length > 0 ? chapters : null, names[0].Title);
            MkvRunResult mux = await tools.MuxAsync(MkvToolNix.BuildMuxArguments(plan, identified), new Progress<double>(p => MuxStep.Progress = p), default);
            if (!mux.Succeeded)
            {
                Fail(MuxStep, string.Join("; ", mux.Errors));
                return;
            }

            outputs.Add((mkv, SubtitleDelivery.Embedded));
            Done(MuxStep, Path.GetFileName(mkv) + (mux.Warnings.Count > 0 ? " " + Loc.T("pipeline.mux.warnings", mux.Warnings.Count, mux.Warnings[0]) : string.Empty));

            // 4. Burn the 内嵌 MP4s, one per script, from the source (or the given encode).
            if (!MakeBurned || Scripts.Count == 0)
            {
                Skip(BurnStep, Loc.T("pipeline.burn.skipped"));
            }
            else
            {
                Begin(BurnStep);
                var written = new List<string>();
                foreach (string script in Scripts)
                {
                    bool traditional = (SubtitleNaming.Guess(script)?.Name ?? string.Empty).EndsWith("TC", StringComparison.Ordinal);
                    ReleaseName name = traditional ? names[2] : names[1];
                    EncodePreset burned = GreenTeaSpec.Preset(Profile, SubtitleDelivery.Burned);
                    burned = burned with
                    {
                        Video = burned.Video with { Quality = (double)Crf },
                        Burn = new SubtitleBurn { File = script, FontsDirectory = FontDirectory.Length > 0 ? FontDirectory : null },
                        Streams = new StreamSettings { ChaptersFile = ffmetadata.Length > 0 ? ffmetadata : null, KeepMetadata = false, Title = name.Title },
                    };
                    string mp4 = Path.Combine(OutputDirectory, name.FileName);
                    EncodeJob job = await RunEncodeAsync(BurnStep, new EncodeJob(Video, FfmpegCommandBuilder.Build(burned, Video, source, mp4, work), name.FileName));
                    if (job.State != EncodeJobState.Done)
                    {
                        Fail(BurnStep, job.Error ?? Loc.T("encode.row.cancelled"));
                        return;
                    }

                    outputs.Add((mp4, SubtitleDelivery.Burned));
                    written.Add(Path.GetFileName(mp4));
                }

                Done(BurnStep, string.Join(", ", written));
            }

            // 5. Check every file against the rules.
            Begin(VerifyStep);
            int fails = 0;
            var problems = new List<string>();
            foreach ((string path, SubtitleDelivery delivery) in outputs)
            {
                IReadOnlyList<SpecFinding> findings = GreenTeaSpec.Check(await Task.Run(() => _read(path)), Profile, delivery);
                fails += findings.Count(f => f.Level == FindingLevel.Fail);
                problems.AddRange(findings.Where(f => f.Level != FindingLevel.Pass).Select(f => $"{Path.GetFileName(path)}: {f.Message}"));
            }

            if (fails > 0)
            {
                Fail(VerifyStep, string.Join(Environment.NewLine, problems));
                return;
            }

            Done(VerifyStep, problems.Count > 0 ? string.Join(Environment.NewLine, problems) : Loc.T("pipeline.verify.passed", outputs.Count));

            // 6. VMAF of the encode against its source.
            string reference = AlreadyEncoded ? Reference : Video;
            if (!RunVmaf || reference.Length == 0)
            {
                Skip(VmafStep, Loc.T("pipeline.vmaf.noReference"));
            }
            else
            {
                Begin(VmafStep);
                string ffmpeg = await VmafRunner.ResolveFfmpegAsync(Compare.FfmpegPath, Settings.Services.Processes);
                var vmafOptions = new VmafOptions
                {
                    FfmpegPath = ffmpeg,
                    Backend = Compare.SelectedBackend,
                    Model = Compare.SelectedModel,
                    ProcessRunner = Settings.Services.Processes,
                };
                VmafReport report = await VmafRunner.RunAsync(reference, encoded, vmafOptions,
                    new Progress<VmafProgress>(p => VmafStep.Detail = p.FrameNumber is int f ? Loc.T("pipeline.vmaf.frame", f) : VmafStep.Detail));
                Done(VmafStep, Loc.T("pipeline.vmaf.done", VmafModels.ShortName(report.Model), report.Stats.Mean, report.Stats.P1, report.Stats.Min, report.Backend.ToDisplayName()));
            }

            Status = Loc.T("pipeline.status.ready", OutputDirectory);
            Toast(new ToastRequest(ToastKind.Success, Loc.T("pipeline.toast.done", names[0].FileName)));
        }
        catch (Exception ex) // any failure stops the run at the step that was running
        {
            if (Steps.FirstOrDefault(s => s.IsRunning) is { } running)
            {
                Fail(running, ex.Message);
            }

            Status = Loc.T("pipeline.status.stopped", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanOpenInCompare() => !IsBusy && EncodedFile.Length > 0 && File.Exists(EncodedFile);

    [RelayCommand(CanExecute = nameof(CanOpenInCompare))]
    private async Task OpenInCompareAsync()
    {
        CompareRequested?.Invoke(this, EventArgs.Empty);
        string reference = AlreadyEncoded ? Reference : Video;
        await Compare.HandleDropAsync(reference.Length > 0 ? [reference, EncodedFile] : [EncodedFile], DropZone.Reference);
    }

    /// <summary>
    /// The chapters as Matroska XML (for mkvmerge) and ffmetadata (for the MP4 encodes), from the chapter
    /// file or the source's own chapters. Both empty when there are none.
    /// </summary>
    private string ChapterFiles(MediaInfo source, string work, out string ffmetadata)
    {
        IReadOnlyList<ChapterMark> marks = ChaptersFile.Length > 0 ? ChapterList.Load(ChaptersFile) : ChapterList.FromMedia(source);
        if (marks.Count == 0)
        {
            ffmetadata = string.Empty;
            return string.Empty;
        }

        string xml = Path.Combine(work, "chapters.xml");
        ffmetadata = Path.Combine(work, "chapters.ffmetadata");
        ChapterList.Save(xml, marks);
        File.WriteAllText(ffmetadata, ChapterList.ToFfMetadata(marks, source.Duration));
        return xml;
    }

    /// <summary>Runs an encode through the shared queue (so it shows on Encode and Monitor) and waits for it.</summary>
    private async Task<EncodeJob> RunEncodeAsync(PipelineStep step, EncodeJob job)
    {
        var finished = new TaskCompletionSource();
        void OnChanged(EncodeJob changed)
        {
            if (changed != job)
            {
                return;
            }

            step.Progress = changed.Progress;
            if (changed.State is EncodeJobState.Done or EncodeJobState.Failed or EncodeJobState.Cancelled)
            {
                finished.TrySetResult();
            }
        }

        Queue.JobChanged += OnChanged;
        try
        {
            Queue.Add(job);
            await finished.Task;
            return job;
        }
        finally
        {
            Queue.JobChanged -= OnChanged;
        }
    }

    private void ResetSteps()
    {
        foreach (PipelineStep step in Steps)
        {
            step.State = StepState.Pending;
            step.Detail = string.Empty;
            step.Progress = 0;
        }
    }

    private void Begin(PipelineStep step)
    {
        step.State = StepState.Running;
        step.Progress = 0;
        Status = step.Title + "…";
    }

    private static void Done(PipelineStep step, string detail)
    {
        step.State = StepState.Done;
        step.Progress = 1;
        step.Detail = detail;
    }

    private void Fail(PipelineStep step, string detail)
    {
        step.State = StepState.Failed;
        step.Detail = detail;
        Status = Loc.T("pipeline.status.stepFailed", step.Title);
        Toast(new ToastRequest(ToastKind.Error, Loc.T("pipeline.toast.stepFailed", step.Title, detail)));
    }

    private void Toast(ToastRequest request) => ToastRequested?.Invoke(this, request);

    private static void Skip(PipelineStep step, string detail)
    {
        step.State = StepState.Skipped;
        step.Detail = detail;
    }
}
