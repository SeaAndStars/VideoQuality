using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentIcons.Common;
using VideoQuality.Core.Mkv;

namespace VideoQuality.App.ViewModels;

/// <summary>One track of a mux input, with the metadata it will carry in the output.</summary>
public partial class MuxTrackRow : ObservableObject
{
    public required int Id { get; init; }

    public required MkvTrackType Type { get; init; }

    public required Symbol Icon { get; init; }

    public required string Codec { get; init; }

    public required string Details { get; init; }

    [ObservableProperty]
    public partial bool Include { get; set; } = true;

    [ObservableProperty]
    public partial string Language { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool Default { get; set; }

    [ObservableProperty]
    public partial bool Forced { get; set; }
}

/// <summary>One source file of the mux.</summary>
public partial class MuxInputRow : ObservableObject
{
    public required string Path { get; init; }

    public required MkvFileInfo Info { get; init; }

    public string FileName => System.IO.Path.GetFileName(Path);

    public ObservableCollection<MuxTrackRow> Tracks { get; } = [];

    [ObservableProperty]
    public partial bool KeepAttachments { get; set; } = true;

    [ObservableProperty]
    public partial bool KeepChapters { get; set; } = true;

    public bool HasAttachments => Info.Attachments.Count > 0;

    public bool HasChapters => Info.ChapterEntries > 0;
}

/// <summary>Mux page (混流): video, audio, subtitles, fonts and chapters into one MKV with mkvmerge.</summary>
public partial class MuxViewModel : ObservableObject
{
    private static readonly string[] FontExtensions = [".ttf", ".otf", ".ttc", ".otc"];

    public MuxViewModel(SettingsViewModel settings)
    {
        Settings = settings;
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.MkvToolNix))
            {
                MuxCommand.NotifyCanExecuteChanged();
            }
        };
        Inputs.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasInputs));
            MuxCommand.NotifyCanExecuteChanged();
        };
        Attachments.CollectionChanged += (_, _) => OnPropertyChanged(nameof(AttachmentSummary));
    }

    public SettingsViewModel Settings { get; }

    public ObservableCollection<MuxInputRow> Inputs { get; } = [];

    public ObservableCollection<string> Attachments { get; } = [];

    [ObservableProperty]
    public partial string Output { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Chapters { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MuxCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = "Drop the video, audio, subtitles and fonts to mux.";

    public bool HasInputs => Inputs.Count > 0;

    public string AttachmentSummary => Attachments.Count == 0
        ? "No fonts attached."
        : $"{Attachments.Count} font(s), {FontSubsetViewModel.Size(Attachments.Where(File.Exists).Sum(f => new FileInfo(f).Length))}";

    public event EventHandler<ToastRequest>? ToastRequested;

    /// <summary>Adds dropped files: fonts and font folders become attachments, .xml chapters, the rest inputs.</summary>
    public async Task AddPathsAsync(IEnumerable<string?> paths)
    {
        foreach (string path in paths.OfType<string>())
        {
            if (Directory.Exists(path))
            {
                AddFonts(Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories));
            }
            else if (FontExtensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            {
                AddFonts([path]);
            }
            else if (System.IO.Path.GetExtension(path).Equals(".xml", StringComparison.OrdinalIgnoreCase))
            {
                Chapters = path;
            }
            else if (File.Exists(path))
            {
                await AddInputAsync(path);
            }
        }
    }

    /// <summary>The Font Subset page's result: renamed scripts become inputs, subset fonts attachments.</summary>
    public async Task AddSubsetOutputAsync(IEnumerable<string> scripts, IEnumerable<string> fonts)
    {
        foreach (string script in scripts)
        {
            await AddInputAsync(script);
        }

        AddFonts(fonts);
        Status = HasVideo() ? Status : "Scripts and fonts added. Drop the encoded video to finish the mux.";
    }

    private bool HasVideo() => Inputs.Any(i => i.Tracks.Any(t => t.Type == MkvTrackType.Video));

    private void AddFonts(IEnumerable<string> files)
    {
        foreach (string file in files.Where(f => FontExtensions.Contains(System.IO.Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)))
        {
            string full = System.IO.Path.GetFullPath(file);
            if (!Attachments.Contains(full, StringComparer.OrdinalIgnoreCase))
            {
                Attachments.Add(full);
            }
        }
    }

    private async Task AddInputAsync(string path)
    {
        if (Settings.MkvToolNix is not { } tools)
        {
            Status = "mkvtoolnix is required: install it or set its folder in Settings.";
            return;
        }

        string full = System.IO.Path.GetFullPath(path);
        if (Inputs.Any(i => string.Equals(i.Path, full, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        try
        {
            MkvFileInfo info = await tools.IdentifyAsync(full);
            var row = new MuxInputRow { Path = full, Info = info };
            (string Language, string Name)? guess = SubtitleNaming.Guess(full);
            string? guessedLanguage = guess?.Language;
            string? guessedName = guess?.Name;
            bool firstSubtitle = !Inputs.SelectMany(i => i.Tracks).Any(t => t.Type == MkvTrackType.Subtitles && t.Default);

            foreach (MkvTrack track in info.Tracks)
            {
                bool isSubtitle = track.Type == MkvTrackType.Subtitles;
                row.Tracks.Add(new MuxTrackRow
                {
                    Id = track.Id,
                    Type = track.Type,
                    Icon = track.Type switch
                    {
                        MkvTrackType.Video => Symbol.Video,
                        MkvTrackType.Audio => Symbol.Speaker2,
                        MkvTrackType.Subtitles => Symbol.ClosedCaption,
                        _ => Symbol.Document,
                    },
                    Codec = track.Codec,
                    Details = track.Details,
                    Language = isSubtitle && guessedLanguage is not null && track.Language is "" or "und"
                        ? guessedLanguage
                        : track.Language,
                    Name = isSubtitle && guessedName is not null && track.Name.Length == 0 ? guessedName : track.Name,
                    Default = isSubtitle ? firstSubtitle : track.Default,
                    Forced = track.Forced,
                });

                if (isSubtitle && firstSubtitle)
                {
                    firstSubtitle = false;
                }
            }

            Inputs.Add(row);
            if (Output.Length == 0 && info.Tracks.Any(t => t.Type == MkvTrackType.Video))
            {
                string stem = System.IO.Path.GetFileNameWithoutExtension(full);
                string folder = System.IO.Path.GetDirectoryName(full)!;
                Output = System.IO.Path.Combine(folder, stem + (full.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase) ? ".mux.mkv" : ".mkv"));
            }

            Status = $"{Inputs.Count} input(s), {Inputs.Sum(i => i.Tracks.Count)} track(s).";
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or System.Text.Json.JsonException)
        {
            Status = $"Could not read {System.IO.Path.GetFileName(full)}: {ex.Message}";
        }
    }

    [RelayCommand]
    private void RemoveInput(MuxInputRow row) => Inputs.Remove(row);

    [RelayCommand]
    private void RemoveAttachment(string path) => Attachments.Remove(path);

    [RelayCommand]
    private void ClearAll()
    {
        Inputs.Clear();
        Attachments.Clear();
        Output = string.Empty;
        Title = string.Empty;
        Chapters = string.Empty;
        Progress = 0;
        Status = "Drop the video, audio, subtitles and fonts to mux.";
    }

    private bool CanMux() => !IsBusy && Settings.HasMkvToolNix && Inputs.Count > 0;

    [RelayCommand(CanExecute = nameof(CanMux))]
    private async Task MuxAsync()
    {
        if (Settings.MkvToolNix is not { } tools || Output.Length == 0)
        {
            Status = "Choose an output file.";
            return;
        }

        IsBusy = true;
        Progress = 0;
        Status = "Muxing…";
        try
        {
            var plan = new MuxPlan(
                Output,
                Inputs.Select(i => new MuxInput(
                    i.Path,
                    i.Tracks.Where(t => t.Include).Select(t => new MuxTrack(t.Id, t.Language, t.Name, t.Default, t.Forced)).ToList(),
                    i.KeepAttachments,
                    i.KeepChapters)).ToList(),
                Attachments.ToList(),
                Chapters.Length > 0 ? Chapters : null,
                Title.Length > 0 ? Title : null);

            MkvRunResult result = await tools.MuxAsync(
                MkvToolNix.BuildMuxArguments(plan, Inputs.ToDictionary(i => i.Path, i => i.Info)),
                new Progress<double>(p => Progress = p),
                default);

            if (result.Succeeded)
            {
                Progress = 1;
                MkvFileInfo written = await tools.IdentifyAsync(Output);
                Status = $"Wrote {System.IO.Path.GetFileName(Output)}: {written.Tracks.Count} track(s), {written.Attachments.Count} attachment(s)" +
                         (result.Warnings.Count > 0 ? $", {result.Warnings.Count} warning(s): {result.Warnings[0]}" : ".");
                ToastRequested?.Invoke(this, new ToastRequest(ToastKind.Success, $"Muxed {System.IO.Path.GetFileName(Output)}."));
            }
            else
            {
                Status = "Mux failed: " + string.Join("; ", result.Errors);
                ToastRequested?.Invoke(this, new ToastRequest(ToastKind.Error, Status));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or InvalidDataException)
        {
            Status = "Mux failed: " + ex.Message;
            ToastRequested?.Invoke(this, new ToastRequest(ToastKind.Error, Status));
        }
        finally
        {
            IsBusy = false;
        }
    }
}
