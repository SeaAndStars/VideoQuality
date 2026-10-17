using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VideoQuality.Core.Decoding;
using VideoQuality.Core.Edit;
using VideoQuality.Core.Encode;
using VideoQuality.Core.Ingest;
using VideoQuality.Core.Localization;
using VideoQuality.Core.Media;
using VideoQuality.Core.Mkv;
using VideoQuality.Core.Scoring;
using VideoQuality.Core.Tools;

namespace VideoQuality.App.ViewModels;

/// <summary>A file in the join list.</summary>
public sealed record MergeInput(string Path, MediaInfo Info)
{
    public string FileName => System.IO.Path.GetFileName(Path);

    public string Details => $"{MediaInfo.FormatClock(Info.Duration)} · {MediaInfo.FormatSize(Info.Size)}";
}

/// <summary>Trim &amp; Merge page (切割 / 合并): cut a range out of a file, or join files end to end.</summary>
public partial class TrimMergeViewModel : ObservableObject
{
    private readonly Func<string, MediaInfo> _read;
    private readonly Func<string, IReadOnlyList<TimeSpan>> _keyframes;

    public TrimMergeViewModel(
        SettingsViewModel settings,
        EncodeViewModel encode,
        Func<string, MediaInfo>? read = null,
        Func<string, IReadOnlyList<TimeSpan>>? keyframes = null)
    {
        Settings = settings;
        Encode = encode;
        _read = read ?? MediaInfoReader.Read;
        _keyframes = keyframes ?? KeyframeTimes;
    }

    public SettingsViewModel Settings { get; }

    public EncodeViewModel Encode { get; }

    public ObservableCollection<MergeInput> MergeInputs { get; } = [];

    public ObservableCollection<string> MergeProblems { get; } = [];

    // Cut
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSource))]
    public partial MediaInfo? Source { get; set; }

    [ObservableProperty]
    public partial string Start { get; set; } = "00:00:00.000";

    [ObservableProperty]
    public partial string End { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool ReEncode { get; set; }

    [ObservableProperty]
    public partial EncodePreset? CutPreset { get; set; }

    [ObservableProperty]
    public partial string CutOutput { get; set; } = string.Empty;

    // Merge
    [ObservableProperty]
    public partial string MergeOutput { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = Loc.T("trim.status.start");

    public bool HasSource => Source is not null;

    public bool HasMergeInputs => MergeInputs.Count > 0;

    public event EventHandler<ToastRequest>? ToastRequested;

    public async Task OpenCutSourceAsync(string path)
    {
        try
        {
            MediaInfo info = await Task.Run(() => _read(path));
            Source = info;
            Start = "00:00:00.000";
            End = MediaInfo.FormatClock(info.Duration);
            CutOutput = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + ".cut" + Path.GetExtension(path));
            CutPreset ??= Encode.Presets.FirstOrDefault();
            Status = Loc.T("trim.status.source", Path.GetFileName(path), MediaInfo.FormatClock(info.Duration));
        }
        catch (InvalidOperationException ex)
        {
            Status = ex.Message;
        }
    }

    public async Task AddMergeInputsAsync(IEnumerable<string?> paths)
    {
        foreach (string path in paths.OfType<string>().Where(DropRouter.IsVideo))
        {
            try
            {
                MergeInputs.Add(new MergeInput(path, await Task.Run(() => _read(path))));
            }
            catch (InvalidOperationException ex)
            {
                ToastRequested?.Invoke(this, new ToastRequest(ToastKind.Warning, ex.Message));
            }
        }

        if (MergeOutput.Length == 0 && MergeInputs.FirstOrDefault() is { } first)
        {
            MergeOutput = Path.Combine(Path.GetDirectoryName(first.Path)!, Path.GetFileNameWithoutExtension(first.Path) + ".joined.mkv");
        }

        CheckMerge();
    }

    [RelayCommand]
    private void MoveUp(MergeInput input)
    {
        int index = MergeInputs.IndexOf(input);
        if (index > 0)
        {
            MergeInputs.Move(index, index - 1);
            CheckMerge();
        }
    }

    [RelayCommand]
    private void MoveDown(MergeInput input)
    {
        int index = MergeInputs.IndexOf(input);
        if (index >= 0 && index < MergeInputs.Count - 1)
        {
            MergeInputs.Move(index, index + 1);
            CheckMerge();
        }
    }

    [RelayCommand]
    private void RemoveMergeInput(MergeInput input)
    {
        MergeInputs.Remove(input);
        CheckMerge();
    }

    /// <summary>Cuts by stream copy right away, or queues a frame-exact re-encode.</summary>
    [RelayCommand]
    private async Task CutAsync()
    {
        if (Source is not { } source)
        {
            return;
        }

        double? start = PresetEditor.ParseTime(Start);
        double? end = PresetEditor.ParseTime(End);
        if (start is null || end is null || end <= start)
        {
            Status = Loc.T("edit.error.range");
            return;
        }

        if (!RequireEncoder())
        {
            return;
        }

        try
        {
            if (ReEncode)
            {
                EncodeCommand command = TrimMerge.EncodeCut(CutPreset ?? Encode.Editor.ToPreset(), source.Path, source, CutOutput, Path.GetTempPath(),
                    TimeSpan.FromSeconds(start.Value), TimeSpan.FromSeconds(end.Value));
                Encode.Queue.Add(new EncodeJob(source.Path, command, Path.GetFileName(CutOutput)));
                Status = Loc.T("trim.status.queued");
                return;
            }

            EditCommand cut = TrimMerge.CopyCut(source.Path, CutOutput, TimeSpan.FromSeconds(start.Value), TimeSpan.FromSeconds(end.Value), _keyframes(source.Path));
            foreach (string warning in cut.Warnings)
            {
                ToastRequested?.Invoke(this, new ToastRequest(ToastKind.Warning, warning));
            }

            await RunAsync(cut, Settings.Encoder!.Path);
        }
        catch (ArgumentException ex)
        {
            Status = ex.Message;
        }
    }

    [RelayCommand]
    private async Task MergeAsync()
    {
        if (MergeInputs.Count < 2)
        {
            Status = Loc.T("edit.merge.needTwo");
            return;
        }

        EditCommand merge = TrimMerge.Merge(MergeInputs.Select(i => i.Info).ToList(), MergeOutput, Path.GetTempPath());
        string? program = merge.UsesMkvMerge ? Settings.MkvToolNix?.MkvMerge : Settings.Encoder?.Path;
        if (program is null)
        {
            Status = merge.UsesMkvMerge ? Loc.T("common.mkvtoolnixRequired") : Loc.T("encode.error.noEncoder");
            return;
        }

        try
        {
            await RunAsync(merge, program);
        }
        finally
        {
            foreach (string file in merge.TemporaryFiles)
            {
                FileSystem.TryDelete(file);
            }
        }
    }

    private bool RequireEncoder()
    {
        if (Settings.HasEncoder)
        {
            return true;
        }

        Status = Loc.T("encode.error.noEncoder");
        return false;
    }

    private void CheckMerge()
    {
        MergeProblems.Clear();
        if (MergeInputs.Count >= 2)
        {
            foreach (string problem in TrimMerge.Compatibility(MergeInputs.Select(i => i.Info).ToList()))
            {
                MergeProblems.Add(problem);
            }
        }

        OnPropertyChanged(nameof(HasMergeInputs));
    }

    private async Task RunAsync(EditCommand command, string program)
    {
        IsBusy = true;
        Progress = 0;
        Status = Loc.T("trim.status.running", Path.GetFileName(command.Output));
        var parser = new FfmpegProgressParser();
        double duration = Math.Max(command.Duration.TotalSeconds, 0.001);
        try
        {
            ProcessResult result = await Settings.Services.Processes.RunAsync(new ProcessRequest
            {
                FileName = program,
                Arguments = command.Arguments,
                OnOutputLine = line =>
                {
                    if (command.UsesMkvMerge)
                    {
                        if (MkvToolNix.ParseGuiLine(line).Progress is double fraction)
                        {
                            Avalonia.Threading.Dispatcher.UIThread.Post(() => Progress = fraction);
                        }
                    }
                    else if (parser.Consume(line) is not null)
                    {
                        double value = Math.Clamp(parser.OutTime.TotalSeconds / duration, 0, 1);
                        Avalonia.Threading.Dispatcher.UIThread.Post(() => Progress = value);
                    }
                },
            });

            bool ok = result.ExitCode == 0 || (command.UsesMkvMerge && result.ExitCode == 1);
            Progress = ok ? 1 : Progress;
            Status = ok
                ? Loc.T("trim.status.done", command.Output)
                : Loc.T("trim.status.failed", (result.Error.Length > 0 ? result.Error : result.Output).Trim());
            ToastRequested?.Invoke(this, ok
                ? new ToastRequest(ToastKind.Success, Loc.T("trim.toast.done", Path.GetFileName(command.Output)))
                : new ToastRequest(ToastKind.Error, Status));
        }
        catch (ProcessStartException ex)
        {
            Status = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Keyframe times from the packet index (frame number ÷ frame rate).</summary>
    private static IReadOnlyList<TimeSpan> KeyframeTimes(string path)
    {
        try
        {
            PacketIndex index = PacketIndex.Build(path);
            double fps = index.DurationSeconds > 0 ? index.FrameCount / index.DurationSeconds : 0;
            return fps > 0 ? index.KeyframeIndices.Select(i => TimeSpan.FromSeconds(i / fps)).ToList() : [];
        }
        catch (InvalidOperationException)
        {
            return [];
        }
    }
}
