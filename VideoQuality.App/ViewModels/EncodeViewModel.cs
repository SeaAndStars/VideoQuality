using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VideoQuality.Core.Encode;
using VideoQuality.Core.Ingest;
using VideoQuality.Core.Localization;
using VideoQuality.Core.Media;

namespace VideoQuality.App.ViewModels;

/// <summary>A file waiting to be encoded.</summary>
public sealed record EncodeInput(string Path, MediaInfo Info)
{
    public string FileName => System.IO.Path.GetFileName(Path);

    public string Details => string.Join(" · ", new[]
    {
        Info.Video is { } v ? $"{v.Codec} {v.Width}×{v.Height}" : string.Empty,
        MediaInfo.FormatClock(Info.Duration),
        MediaInfo.FormatSize(Info.Size),
    }.Where(s => s.Length > 0));
}

/// <summary>A queue entry as the list shows it; refreshed from the queue's events.</summary>
public partial class EncodeJobRow(EncodeJob job) : ObservableObject
{
    public EncodeJob Job { get; } = job;

    public string Name => Job.Name;

    public string Output => Job.Output;

    [ObservableProperty]
    public partial EncodeJobState State { get; private set; } = job.State;

    [ObservableProperty]
    public partial double Progress { get; private set; }

    [ObservableProperty]
    public partial string Details { get; private set; } = string.Empty;

    public bool IsRunning => State == EncodeJobState.Running;

    public bool IsPaused => State == EncodeJobState.Paused;

    public bool IsActive => State is EncodeJobState.Running or EncodeJobState.Paused;

    public bool IsQueued => State == EncodeJobState.Queued;

    public bool IsDone => State == EncodeJobState.Done;

    public bool IsFailed => State is EncodeJobState.Failed or EncodeJobState.Cancelled;

    public void Refresh()
    {
        State = Job.State;
        Progress = Job.Progress;
        Details = Job.State switch
        {
            EncodeJobState.Running or EncodeJobState.Paused => Loc.T("encode.row.running",
                Job.Pass, Job.Command.Passes.Count, (Job.Progress * 100).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
                Job.Fps.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture),
                Job.Speed.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                Job.Remaining is TimeSpan left ? MediaInfo.FormatClock(left)[..8] : "--:--:--",
                Job.EstimatedSize > 0 ? MediaInfo.FormatSize(Job.EstimatedSize) : "…"),
            EncodeJobState.Done => Loc.T("encode.row.done", MediaInfo.FormatSize(Job.WrittenSize), MediaInfo.FormatClock(Job.Elapsed)[..8]),
            EncodeJobState.Failed => Job.Error!, // the queue always says why
            EncodeJobState.Cancelled => Loc.T("encode.row.cancelled"),
            _ => Loc.T("encode.row.queued"),
        };
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(IsQueued));
        OnPropertyChanged(nameof(IsDone));
        OnPropertyChanged(nameof(IsFailed));
    }
}

/// <summary>
/// Encode page (压制): pick or edit a preset, add files, and run them through the encode queue
/// shared with Trim &amp; Merge and Pipeline.
/// </summary>
public partial class EncodeViewModel : ObservableObject
{
    private readonly Func<string, MediaInfo> _read;
    private readonly Action<Action> _post;

    public EncodeViewModel(SettingsViewModel settings, EncodeQueue queue, PresetStore store, Func<string, MediaInfo>? read = null, Action<Action>? post = null)
    {
        Settings = settings;
        Queue = queue;
        Store = store;
        _read = read ?? MediaInfoReader.Read;
        _post = post ?? (action => Avalonia.Threading.Dispatcher.UIThread.Post(action));
        Editor.Changed += (_, _) => UpdatePreview();
        Queue.JobChanged += job => _post(() => OnJobChanged(job));
        Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.Encoder))
            {
                Editor.SetAvailable(Settings.Encoder);
            }
            else if (e.PropertyName == nameof(SettingsViewModel.EncodeConcurrency))
            {
                Queue.Concurrency = Settings.EncodeConcurrency;
            }
        };
        Queue.Concurrency = Settings.EncodeConcurrency;
        Editor.SetAvailable(Settings.Encoder);
        ReloadPresets();
        SelectedPreset = Presets[0];
    }

    public SettingsViewModel Settings { get; }

    public EncodeQueue Queue { get; }

    public PresetStore Store { get; }

    public PresetEditor Editor { get; } = new();

    public ObservableCollection<EncodePreset> Presets { get; } = [];

    public ObservableCollection<EncodeInput> Inputs { get; } = [];

    public ObservableCollection<EncodeJobRow> Jobs { get; } = [];

    public ObservableCollection<string> Warnings { get; } = [];

    [ObservableProperty]
    public partial EncodePreset? SelectedPreset { get; set; }

    [ObservableProperty]
    public partial string OutputDirectory { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CommandPreview { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Status { get; set; } = Loc.T("encode.status.start");

    [ObservableProperty]
    public partial string SaveName { get; set; } = string.Empty;

    public bool HasInputs => Inputs.Count > 0;

    public bool HasJobs => Jobs.Count > 0;

    public event EventHandler<ToastRequest>? ToastRequested;

    /// <summary>Asks the shell to open the folder of a file.</summary>
    public event EventHandler<string>? RevealRequested;

    /// <summary>Asks the shell to score an output against its source in Compare.</summary>
    public event EventHandler<(string Source, string Output)>? CompareRequested;

    partial void OnSelectedPresetChanged(EncodePreset? value)
    {
        if (value is not null)
        {
            Editor.Load(value);
            SaveName = value.BuiltIn ? value.Name + " (copy)" : value.Name;
        }
    }

    partial void OnOutputDirectoryChanged(string value) => UpdatePreview();

    public void ReloadPresets(string? select = null)
    {
        Presets.Clear();
        foreach (EncodePreset preset in Store.All())
        {
            Presets.Add(preset);
        }

        if (select is not null)
        {
            SelectedPreset = Presets.FirstOrDefault(p => p.Name == select) ?? Presets[0];
        }
    }

    /// <summary>Adds video files (folders are searched one level deep).</summary>
    public async Task AddPathsAsync(IEnumerable<string?> paths)
    {
        var files = new List<string>();
        foreach (string path in paths.OfType<string>())
        {
            if (Directory.Exists(path))
            {
                files.AddRange(Directory.EnumerateFiles(path).Where(DropRouter.IsVideo).Order(StringComparer.OrdinalIgnoreCase));
            }
            else if (DropRouter.IsVideo(path))
            {
                files.Add(path);
            }
        }

        foreach (string file in files.Where(f => Inputs.All(i => i.Path != f)))
        {
            try
            {
                Inputs.Add(new EncodeInput(file, await Task.Run(() => _read(file))));
            }
            catch (InvalidOperationException ex)
            {
                Toast(new ToastRequest(ToastKind.Warning, ex.Message));
            }
        }

        OnPropertyChanged(nameof(HasInputs));
        Status = Loc.T("encode.status.inputs", Inputs.Count);
        UpdatePreview();
    }

    [RelayCommand]
    private void RemoveInput(EncodeInput input)
    {
        Inputs.Remove(input);
        OnPropertyChanged(nameof(HasInputs));
        UpdatePreview();
    }

    /// <summary>Queues every input with the edited preset.</summary>
    [RelayCommand]
    private void Start()
    {
        if (!Settings.HasEncoder)
        {
            Toast(new ToastRequest(ToastKind.Error, Loc.T("encode.error.noEncoder")));
            return;
        }

        EncodePreset preset = Editor.ToPreset();
        int added = 0;
        foreach (EncodeInput input in Inputs.ToList())
        {
            try
            {
                EncodeCommand command = FfmpegCommandBuilder.Build(preset, input.Path, input.Info, OutputFor(preset, input.Path), Path.GetTempPath());
                Queue.Add(new EncodeJob(input.Path, command));
                Inputs.Remove(input);
                added++;
            }
            catch (ArgumentException ex)
            {
                Toast(new ToastRequest(ToastKind.Error, ex.Message));
                break;
            }
        }

        OnPropertyChanged(nameof(HasInputs));
        Status = Loc.T("encode.status.queued", added);
        UpdatePreview();
    }

    [RelayCommand]
    private void Pause(EncodeJobRow row) => Queue.Pause(row.Job);

    [RelayCommand]
    private void Resume(EncodeJobRow row) => Queue.Resume(row.Job);

    [RelayCommand]
    private void Stop(EncodeJobRow row) => Queue.Stop(row.Job);

    [RelayCommand]
    private void Retry(EncodeJobRow row) => Queue.Retry(row.Job);

    [RelayCommand]
    private void RemoveJob(EncodeJobRow row)
    {
        if (Queue.Remove(row.Job))
        {
            Jobs.Remove(row);
            OnPropertyChanged(nameof(HasJobs));
        }
    }

    [RelayCommand]
    private void ClearFinished()
    {
        foreach (EncodeJobRow row in Jobs.Where(r => r.IsDone || r.IsFailed).ToList())
        {
            RemoveJob(row);
        }
    }

    [RelayCommand]
    private void Reveal(EncodeJobRow row) => RevealRequested?.Invoke(this, row.Output);

    [RelayCommand]
    private void ScoreInCompare(EncodeJobRow row) => CompareRequested?.Invoke(this, (row.Job.Source, row.Output));

    [RelayCommand]
    private void SavePreset()
    {
        EncodePreset saved = Store.Save(Editor.ToPreset() with { Name = SaveName.Trim() });
        ReloadPresets(saved.Name);
        Toast(new ToastRequest(ToastKind.Success, Loc.T("encode.preset.saved", saved.Name)));
    }

    [RelayCommand]
    private void DeletePreset()
    {
        if (SelectedPreset is { BuiltIn: false } preset && Store.Delete(preset.Name))
        {
            ReloadPresets(Presets[0].Name);
        }
    }

    private void Toast(ToastRequest request) => ToastRequested?.Invoke(this, request);

    private string OutputFor(EncodePreset preset, string source) =>
        FfmpegCommandBuilder.OutputPath(preset, source, string.IsNullOrWhiteSpace(OutputDirectory) ? null : OutputDirectory.Trim());

    private void UpdatePreview()
    {
        Warnings.Clear();
        if (Inputs.FirstOrDefault() is not { } input)
        {
            CommandPreview = Loc.T("encode.preview.empty");
            return;
        }

        try
        {
            EncodePreset preset = Editor.ToPreset();
            EncodeCommand command = FfmpegCommandBuilder.Build(preset, input.Path, input.Info, OutputFor(preset, input.Path), Path.GetTempPath());
            string executable = Settings.Encoder?.Path ?? "ffmpeg";
            CommandPreview = string.Join(Environment.NewLine + Environment.NewLine,
                command.Passes.Select(pass => FfmpegCommandBuilder.FormatCommandLine(executable, pass)));
            foreach (string warning in command.Warnings)
            {
                Warnings.Add(warning);
            }
        }
        catch (ArgumentException ex)
        {
            CommandPreview = ex.Message;
        }
    }

    private void OnJobChanged(EncodeJob job)
    {
        EncodeJobRow? row = Jobs.FirstOrDefault(r => r.Job == job);
        if (row is null)
        {
            row = new EncodeJobRow(job);
            Jobs.Add(row);
            OnPropertyChanged(nameof(HasJobs));
        }

        EncodeJobState before = row.State;
        row.Refresh();
        if (before != row.State && row.State == EncodeJobState.Done)
        {
            Toast(new ToastRequest(ToastKind.Success, Loc.T("encode.toast.done", job.Name)));
        }
        else if (before != row.State && row.State == EncodeJobState.Failed)
        {
            Toast(new ToastRequest(ToastKind.Error, Loc.T("encode.toast.failed", job.Name)));
        }
    }
}
