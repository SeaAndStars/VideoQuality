using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VideoQuality.Core.Chapters;
using VideoQuality.Core.Diagnostics;
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

/// <summary>The video encoder the pipeline uses.</summary>
public enum PipelineEncoder
{
    /// <summary>x265 10-bit CRF, as the rules require.</summary>
    X265,

    /// <summary>hevc_nvenc UHQ at constant quality; outside the rules, for comparison.</summary>
    NvencUhq,
}

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

    [ObservableProperty]
    public required partial string Description { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPending), nameof(IsRunning), nameof(IsDone), nameof(IsFailed), nameof(IsSkipped))]
    public partial StepState State { get; set; }

    [ObservableProperty]
    public partial string Detail { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double Progress { get; set; }

    /// <summary>Off: the run skips this step.</summary>
    [ObservableProperty]
    public partial bool Enabled { get; set; } = true;

    public bool IsPending => State == StepState.Pending;

    public bool IsRunning => State == StepState.Running;

    public bool IsDone => State == StepState.Done;

    public bool IsFailed => State == StepState.Failed;

    public bool IsSkipped => State == StepState.Skipped;
}

/// <summary>One 内嵌 MP4 to make: the script burned into it and the language tag its name carries.</summary>
public partial class BurnTarget : ObservableObject
{
    [ObservableProperty]
    public partial string Script { get; set; } = string.Empty;

    /// <summary><c>JPSC</c>, <c>JPTC</c>, <c>SC</c> or <c>TC</c>.</summary>
    [ObservableProperty]
    public partial string Language { get; set; } = "JPSC";

    /// <summary>The release file name, kept current by the pipeline.</summary>
    [ObservableProperty]
    public partial string FileName { get; set; } = string.Empty;

    /// <summary>The release's name for this MP4 (AAC audio, the language tag from <see cref="Language"/>).</summary>
    public ReleaseName Name(ReleaseName release) => release with
    {
        Delivery = SubtitleDelivery.Burned,
        AudioCodec = "AAC",
        Subtitles = Language.StartsWith("JP", StringComparison.Ordinal) ? SubtitleSet.Bilingual : SubtitleSet.Monolingual,
        Script = Language.EndsWith("TC", StringComparison.Ordinal) ? ChineseScript.Traditional : ChineseScript.Simplified,
    };
}

/// <summary>
/// The group's release flow in one run: encode the source (x265 10-bit CRF), subset the scripts'
/// fonts, mux the 内封 MKV, burn the 内嵌 MP4s, check every file against the rules, and score the
/// encode against the source with VMAF. File names come from the Naming page.
/// </summary>
public partial class PipelineViewModel : ObservableObject
{
    /// <summary>Audio a web source never carries: a lossless track means a disc.</summary>
    private static readonly HashSet<string> LosslessAudio = ["flac", "truehd", "alac", "pcm_s16le", "pcm_s24le", "pcm_s32le"];

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
        Scripts.CollectionChanged += (_, _) => AddBurnTargetCommand.NotifyCanExecuteChanged();
        naming.PropertyChanged += (_, _) => RefreshNames();
        Burns.CollectionChanged += (_, _) => RefreshNames();
        foreach (PipelineStep step in Steps)
        {
            step.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PipelineStep.Enabled))
                {
                    OnPropertyChanged(nameof(AlreadyEncoded));
                    OnPropertyChanged(nameof(MakeMkv));
                    OnPropertyChanged(nameof(MakeBurned));
                    OnPropertyChanged(nameof(RunVmaf));
                    RefreshNames();
                }
            };
        }
    }

    public SettingsViewModel Settings { get; }

    public MainViewModel Compare { get; }

    public EncodeQueue Queue { get; }

    public NamingViewModel Naming { get; }

    public ObservableCollection<string> Scripts { get; } = [];

    public IReadOnlyList<ReleaseProfile> Profiles { get; } = Enum.GetValues<ReleaseProfile>();

    public IReadOnlyList<PipelineEncoder> Encoders { get; } = Enum.GetValues<PipelineEncoder>();

    /// <summary>The files the last run wrote, in order (MKV first).</summary>
    public ObservableCollection<string> Outputs { get; } = [];

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
    public bool AlreadyEncoded
    {
        get => !EncodeStep.Enabled;
        set => EncodeStep.Enabled = !value;
    }

    /// <summary>Mux the 内封 MKV.</summary>
    public bool MakeMkv
    {
        get => MuxStep.Enabled;
        set => MuxStep.Enabled = value;
    }

    /// <summary>Burn the 内嵌 MP4s in <see cref="Burns"/>.</summary>
    public bool MakeBurned
    {
        get => BurnStep.Enabled;
        set => BurnStep.Enabled = value;
    }

    public bool RunVmaf
    {
        get => VmafStep.Enabled;
        set => VmafStep.Enabled = value;
    }

    /// <summary>The 内嵌 MP4s to make; one per script to start with, free to add, remove and change.</summary>
    public ObservableCollection<BurnTarget> Burns { get; } = [];

    /// <summary>The language tags an MP4 can carry, the bilingual ones first.</summary>
    public IReadOnlyList<string> BurnLanguages { get; } = ["JPSC", "JPTC", "SC", "TC"];

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
    [NotifyPropertyChangedFor(nameof(IsNvenc))]
    public partial PipelineEncoder Encoder { get; set; }

    public bool IsNvenc => Encoder == PipelineEncoder.NvencUhq;

    /// <summary>x265 CRF, or NVENC CQ.</summary>
    [ObservableProperty]
    public partial decimal Crf { get; set; } = 20;

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
    /// <summary>The 内封 MKV's file name.</summary>
    public string MkvName => Naming.Set()[0].FileName;

    public IReadOnlyList<string> OutputNames =>
        [.. MakeMkv ? [Naming.Set()[0].FileName] : Array.Empty<string>(), .. MakeBurned ? Burns.Select(b => b.FileName) : []];

    public event EventHandler<ToastRequest>? ToastRequested;

    /// <summary>Asks the window to show a written file in the file manager.</summary>
    public event EventHandler<string>? RevealRequested;

    /// <summary>Raised by "Open in Compare" so the shell can switch pages.</summary>
    public event EventHandler? CompareRequested;

    partial void OnProfileChanged(ReleaseProfile value) => ApplyProfile(value);

    /// <summary>The release kind's defaults: outputs, quality, and the source and audio in the names.</summary>
    private void ApplyProfile(ReleaseProfile value)
    {
        // The rules: BD releases are the 内封 MKV only; web releases add the two 内嵌 MP4s.
        bool bd = GreenTeaSpec.IsBd(value);
        MakeBurned = !bd;
        Crf = DefaultQuality();
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

    partial void OnEncoderChanged(PipelineEncoder value)
    {
        Crf = DefaultQuality();
        EncodeStep.Description = Loc.T(IsNvenc ? "pipeline.step.encode.nvenc" : "pipeline.step.encode.description");
    }

    private decimal DefaultQuality() => IsNvenc ? (decimal)NvencUhq.Quality : GreenTeaSpec.IsBd(Profile) ? 18 : 20;

    /// <summary>The spec preset with the chosen encoder and quality.</summary>
    private EncodePreset Tuned(EncodePreset preset) =>
        IsNvenc ? NvencUhq.Apply(preset, (double)Crf) : preset with { Video = preset.Video with { Quality = (double)Crf } };

    [RelayCommand]
    private void Reveal(string path) => RevealRequested?.Invoke(this, path);

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
                    AddBurn(path);
                }

                if (FontDirectory.Length == 0)
                {
                    FontDirectory = FontsBeside(Path.GetDirectoryName(path)!);
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

    private static string FontsBeside(string folder) =>
        new[] { "Fonts", "fonts", "字体" }.Select(n => Path.Combine(folder, n)).FirstOrDefault(Directory.Exists) ?? string.Empty;

    /// <summary>Uses a video as the source: output folder beside it, everything else inferred from it.</summary>
    public void SetVideo(string path)
    {
        Video = path;
        if (OutputDirectory.Length == 0)
        {
            OutputDirectory = Path.Combine(Path.GetDirectoryName(path)!, "release");
        }

        Inference = InferAsync(path);
    }

    /// <summary>The inference <see cref="SetVideo"/> started; done once the fields are filled.</summary>
    public Task Inference { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Fills in what the source implies: scripts, fonts and chapters beside it that share its name;
    /// title, episode and version from its file name; BD or web and episode or movie from the name, the
    /// audio and the length. A source that is already a release name fills the Naming form as it is.
    /// </summary>
    public async Task InferAsync(string path)
    {
        string folder = Path.GetDirectoryName(path)!;
        string stem = Path.GetFileNameWithoutExtension(path);
        if (Scripts.Count == 0)
        {
            AddPaths(Directory.EnumerateFiles(folder)
                .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".ass" or ".ssa" && Path.GetFileName(f).StartsWith(stem, StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.OrdinalIgnoreCase));
        }

        if (FontDirectory.Length == 0)
        {
            FontDirectory = FontsBeside(folder);
        }

        if (ChaptersFile.Length == 0)
        {
            ChaptersFile = new[] { stem + ".chapters.xml", stem + ".xml", stem + ".chapters.txt", "chapters.xml" }
                .Select(n => Path.Combine(folder, n)).FirstOrDefault(File.Exists) ?? string.Empty;
        }

        if (ReleaseName.Parse(path) is { } release)
        {
            await Naming.FillFromAsync(path);
            Profile = ProfileFor(release.Source.StartsWith("BD", StringComparison.OrdinalIgnoreCase), release.Kind == EpisodeKind.Movie);
            Status = Loc.T("naming.status.parsed");
            return;
        }

        SourceName guess = SourceName.Parse(path);
        MediaInfo? info = null;
        try
        {
            info = await Task.Run(() => _read(path));
        }
        catch (InvalidOperationException ex)
        {
            VqLog.Warn("pipeline", $"cannot read {path}", ex);
        }

        bool bd = guess.Bd || (!guess.Web && LosslessAudio.Contains(info?.AudioStreams.FirstOrDefault()?.Codec ?? string.Empty));
        bool movie = guess.Kind == EpisodeKind.Movie || (guess.Episode is null && info is { Duration.TotalMinutes: > 60 });
        ReleaseProfile profile = ProfileFor(bd, movie);
        Profile = profile;
        ApplyProfile(profile); // also when the kind was already this one: the names follow it
        if (guess.Title.Length > 0)
        {
            Naming.Title = guess.Title;
        }

        if (!movie && guess.Episode is int episode)
        {
            Naming.Kind = guess.Kind;
            Naming.Episode = episode;
        }

        Naming.Version = guess.Version;
        Naming.VideoCodec = "HEVC-10bit";
        Naming.Resolution = "1080p";
        Status = Loc.T("pipeline.status.inferred", Path.GetFileName(path), Loc.T($"label.{nameof(ReleaseProfile)}.{profile}"), Scripts.Count);
    }

    private static ReleaseProfile ProfileFor(bool bd, bool movie) => (bd, movie) switch
    {
        (false, false) => ReleaseProfile.WebEpisode,
        (false, true) => ReleaseProfile.WebMovie,
        (true, false) => ReleaseProfile.BdEpisode,
        _ => ReleaseProfile.BdMovie,
    };

    /// <summary>An MP4 burning <paramref name="script"/>, tagged by the script's name.</summary>
    private void AddBurn(string script)
    {
        string? guessed = SubtitleNaming.Guess(script)?.Name;
        var target = new BurnTarget { Script = script, Language = FreeLanguage(guessed) };
        target.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(BurnTarget.FileName))
            {
                RefreshNames();
            }
        };
        Burns.Add(target);
    }

    /// <summary>The guessed tag when no other MP4 has it yet, else the first one not in use.</summary>
    private string FreeLanguage(string? guessed)
    {
        bool Used(string language) => Burns.Any(b => b.Language == language);
        return !string.IsNullOrEmpty(guessed) && !Used(guessed)
            ? guessed
            : BurnLanguages.FirstOrDefault(l => !Used(l)) ?? BurnLanguages[0];
    }

    private bool CanAddBurn() => Scripts.Count > 0;

    /// <summary>Another MP4: the first script without one (else the first script), with a free language tag.</summary>
    [RelayCommand(CanExecute = nameof(CanAddBurn))]
    private void AddBurnTarget() =>
        AddBurn(Scripts.FirstOrDefault(s => Burns.All(b => !string.Equals(b.Script, s, StringComparison.OrdinalIgnoreCase))) ?? Scripts[0]);

    [RelayCommand]
    private void RemoveBurn(BurnTarget target) => Burns.Remove(target);

    private void RefreshNames()
    {
        ReleaseName release = Naming.Name;
        foreach (BurnTarget target in Burns)
        {
            target.FileName = target.Name(release).FileName;
        }

        OnPropertyChanged(nameof(MkvName));
        OnPropertyChanged(nameof(OutputNames));
    }

    [RelayCommand]
    private void RemoveScript(string path)
    {
        Scripts.Remove(path);
        for (int i = Burns.Count - 1; i >= 0; i--)
        {
            if (Burns[i].Script == path)
            {
                Burns.RemoveAt(i);
            }
        }
    }

    [RelayCommand]
    private void Clear()
    {
        Scripts.Clear();
        Burns.Clear();
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
                EncodePreset preset = Tuned(GreenTeaSpec.Preset(Profile, SubtitleDelivery.Embedded));
                preset = preset with
                {
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
            if (!SubsetStep.Enabled)
            {
                Skip(SubsetStep, Loc.T("pipeline.step.off"));
            }
            else if (Scripts.Count == 0)
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
            if (!MakeMkv)
            {
                Skip(MuxStep, Loc.T("pipeline.step.off"));
            }
            else
            {
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

                List<string> fonts = subset?.Fonts.Select(f => f.OutputPath).ToList() ?? (Scripts.Count > 0 ? FullFonts() : []);
                var plan = new MuxPlan(mkv, inputs, fonts, chapters.Length > 0 ? chapters : null, names[0].Title);
                MkvRunResult mux = await tools.MuxAsync(MkvToolNix.BuildMuxArguments(plan, identified), new Progress<double>(p => MuxStep.Progress = p), default);
                if (!mux.Succeeded)
                {
                    Fail(MuxStep, string.Join("; ", mux.Errors));
                    return;
                }

                outputs.Add((mkv, SubtitleDelivery.Embedded));
                Outputs.Add(mkv);
                Done(MuxStep, Path.GetFileName(mkv) + (mux.Warnings.Count > 0 ? " " + Loc.T("pipeline.mux.warnings", mux.Warnings.Count, mux.Warnings[0]) : string.Empty));
            }

            // 4. Burn the 内嵌 MP4s, one per target, from the source (or the given encode).
            if (!MakeBurned || Burns.Count == 0)
            {
                Skip(BurnStep, Loc.T(MakeBurned ? "pipeline.burn.skipped" : "pipeline.step.off"));
            }
            else
            {
                Begin(BurnStep);
                var written = new List<string>();
                foreach (BurnTarget target in Burns.ToList())
                {
                    string script = target.Script;
                    ReleaseName name = target.Name(names[0]);
                    EncodePreset burned = Tuned(GreenTeaSpec.Preset(Profile, SubtitleDelivery.Burned));
                    burned = burned with
                    {
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
                    Outputs.Add(mp4);
                    written.Add(Path.GetFileName(mp4));
                }

                Done(BurnStep, string.Join(", ", written));
            }

            // 5. Check every file against the rules.
            if (!VerifyStep.Enabled || outputs.Count == 0)
            {
                Skip(VerifyStep, Loc.T(VerifyStep.Enabled ? "pipeline.verify.nothing" : "pipeline.step.off"));
            }
            else
            {
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
            }

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

    /// <summary>Every font file in the font folder: what the MKV carries when fonts are not subset.</summary>
    private List<string> FullFonts() => Directory.Exists(FontDirectory)
        ? [.. Directory.EnumerateFiles(FontDirectory).Where(f => Path.GetExtension(f).ToLowerInvariant() is ".ttf" or ".otf" or ".ttc" or ".otc").Order(StringComparer.Ordinal)]
        : [];

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
        Outputs.Clear();
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
