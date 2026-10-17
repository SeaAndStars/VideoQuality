using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentIcons.Common;
using VideoQuality.Core.Mkv;
using VideoQuality.Core.Localization;

namespace VideoQuality.App.ViewModels;

/// <summary>A track or attachment that can be ticked for extraction.</summary>
public partial class ExtractItem : ObservableObject
{
    public required int Id { get; init; }

    public required bool IsAttachment { get; init; }

    public required Symbol Icon { get; init; }

    public required string Kind { get; init; }

    public required string Title { get; init; }

    public required string Details { get; init; }

    public required string OutputName { get; init; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>Extract page (抽流): pick tracks, attachments (fonts) and chapters out of a file.</summary>
public partial class ExtractViewModel : ObservableObject
{
    public ExtractViewModel(SettingsViewModel settings)
    {
        Settings = settings;
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.MkvToolNix))
            {
                ExtractCommand.NotifyCanExecuteChanged();
            }
        };
    }

    public SettingsViewModel Settings { get; }

    public ObservableCollection<ExtractItem> Items { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSource))]
    public partial string Source { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary), nameof(HasChapters))]
    public partial MkvFileInfo? Info { get; set; }

    [ObservableProperty]
    public partial bool ExtractChapters { get; set; }

    [ObservableProperty]
    public partial string OutputDirectory { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExtractCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = Loc.T("extract.status.start");

    public bool HasSource => Source.Length > 0;

    public bool HasChapters => Info?.ChapterEntries > 0;

    public string Summary => Info is null
        ? string.Empty
        : Loc.T("extract.summary", Info.Container, TimeSpan.FromSeconds(Info.DurationSeconds).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture), Info.Tracks.Count, Info.Attachments.Count) +
          (Info.ChapterEntries > 0 ? " · " + Loc.T("extract.summary.chapters", Info.ChapterEntries) : string.Empty);

    public event EventHandler<ToastRequest>? ToastRequested;

    public async Task OpenAsync(string path)
    {
        if (Settings.MkvToolNix is not { } tools)
        {
            Status = Loc.T("common.mkvtoolnixRequired");
            return;
        }

        IsBusy = true;
        Items.Clear();
        Info = null;
        Source = path;
        Status = Loc.T("extract.status.reading");
        try
        {
            MkvFileInfo info = await tools.IdentifyAsync(path);
            Info = info;
            string stem = Path.GetFileNameWithoutExtension(path);
            foreach (MkvTrack track in info.Tracks)
            {
                string language = track.Language is { Length: > 0 } and not "und" ? "." + track.Language : string.Empty;
                Items.Add(new ExtractItem
                {
                    Id = track.Id,
                    IsAttachment = false,
                    Icon = track.Type switch
                    {
                        MkvTrackType.Video => Symbol.Video,
                        MkvTrackType.Audio => Symbol.Speaker2,
                        MkvTrackType.Subtitles => Symbol.ClosedCaption,
                        _ => Symbol.Document,
                    },
                    Kind = $"#{track.Id} {Loc.T("track.type." + track.Type.ToString().ToLowerInvariant())}",
                    Title = track.Name.Length > 0 ? track.Name : track.Codec,
                    Details = string.Join(" · ", new[] { track.Codec, track.Language, track.Details,
                        track.Default ? Loc.T("track.flag.default") : null, track.Forced ? Loc.T("track.flag.forced") : null }.Where(s => !string.IsNullOrEmpty(s))),
                    OutputName = $"{stem}.track{track.Id}{language}{track.Extension}",
                    IsSelected = track.Type == MkvTrackType.Subtitles,
                });
            }

            foreach (MkvAttachment attachment in info.Attachments)
            {
                Items.Add(new ExtractItem
                {
                    Id = attachment.Id,
                    IsAttachment = true,
                    Icon = attachment.MimeType.Contains("font", StringComparison.OrdinalIgnoreCase) ||
                           Path.GetExtension(attachment.FileName).ToLowerInvariant() is ".ttf" or ".otf" or ".ttc"
                        ? Symbol.TextFont
                        : Symbol.Attach,
                    Kind = Loc.T("extract.attachment", attachment.Id),
                    Title = attachment.FileName,
                    Details = $"{attachment.MimeType} · {FontSubsetViewModel.Size(attachment.Size)}",
                    OutputName = Path.Combine("attachments", attachment.FileName),
                    IsSelected = true,
                });
            }

            ExtractChapters = info.ChapterEntries > 0;
            OutputDirectory = Path.Combine(Path.GetDirectoryName(path)!, stem + "_extracted");
            Status = Summary;
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or System.Text.Json.JsonException)
        {
            Status = Loc.T("extract.status.unreadable", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void SelectKind(string kind)
    {
        foreach (ExtractItem item in Items)
        {
            item.IsSelected = kind switch
            {
                "all" => true,
                "none" => false,
                "subtitles" => item.Icon == Symbol.ClosedCaption,
                "fonts" => item.Icon == Symbol.TextFont,
                _ => item.IsSelected,
            };
        }
    }

    private bool CanExtract() => !IsBusy && Settings.HasMkvToolNix && Info is not null;

    [RelayCommand(CanExecute = nameof(CanExtract))]
    private async Task ExtractAsync()
    {
        if (Settings.MkvToolNix is not { } tools || Info is not { } info)
        {
            return;
        }

        var tracks = Items.Where(i => i.IsSelected && !i.IsAttachment).ToList();
        var attachments = Items.Where(i => i.IsSelected && i.IsAttachment).ToList();
        if (tracks.Count == 0 && attachments.Count == 0 && !ExtractChapters)
        {
            Status = Loc.T("extract.status.nothingSelected");
            return;
        }

        IsBusy = true;
        Progress = 0;
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(OutputDirectory);
            Directory.CreateDirectory(Path.Combine(OutputDirectory, "attachments"));
            string source = Source;

            // mkvextract reads Matroska only; anything else is remuxed to a temporary MKV first.
            if (!info.Container.Contains("Matroska", StringComparison.OrdinalIgnoreCase) &&
                !info.Container.Contains("WebM", StringComparison.OrdinalIgnoreCase))
            {
                Status = Loc.T("extract.status.remuxing", info.Container);
                temporary = Path.Combine(OutputDirectory, $".vq-remux-{Guid.NewGuid():N}.mkv");
                MkvRunResult remux = await tools.MuxAsync(
                    MkvToolNix.BuildMuxArguments(new MuxPlan(temporary, [new MuxInput(source)]), new Dictionary<string, MkvFileInfo>()),
                    new Progress<double>(p => Progress = p * 0.5),
                    default);
                if (!remux.Succeeded)
                {
                    throw new InvalidOperationException(string.Join("; ", remux.Errors));
                }

                source = temporary;
            }

            Status = Loc.T("extract.status.extracting");
            IReadOnlyList<string> args = MkvToolNix.BuildExtractArguments(
                source,
                tracks.Select(t => (t.Id, Path.Combine(OutputDirectory, t.OutputName))),
                attachments.Select(a => (a.Id, Path.Combine(OutputDirectory, a.OutputName))),
                ExtractChapters ? Path.Combine(OutputDirectory, Path.GetFileNameWithoutExtension(Source) + ".chapters.xml") : null);
            double offset = temporary is null ? 0 : 0.5;
            MkvRunResult result = await tools.ExtractAsync(args, new Progress<double>(p => Progress = offset + p * (1 - offset)), default);

            if (result.Succeeded)
            {
                Progress = 1;
                Status = Loc.T("extract.status.done", tracks.Count, attachments.Count, OutputDirectory) +
                         (result.Warnings.Count > 0 ? Loc.T("extract.status.warnings", result.Warnings.Count) : ".");
                ToastRequested?.Invoke(this, new ToastRequest(ToastKind.Success, Loc.T("extract.toast.done")));
            }
            else
            {
                Status = Loc.T("extract.status.failed", string.Join("; ", result.Errors));
                ToastRequested?.Invoke(this, new ToastRequest(ToastKind.Error, Status));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Status = Loc.T("extract.status.failed", ex.Message);
            ToastRequested?.Invoke(this, new ToastRequest(ToastKind.Error, Status));
        }
        finally
        {
            if (temporary is not null && File.Exists(temporary))
            {
                File.Delete(temporary);
            }

            IsBusy = false;
        }
    }
}
