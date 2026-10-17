using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentIcons.Common;
using VideoQuality.Core.Mkv;

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
    public partial string Status { get; set; } = "Drop a video (MKV, MP4, …) to list its tracks and attachments.";

    public bool HasSource => Source.Length > 0;

    public bool HasChapters => Info?.ChapterEntries > 0;

    public string Summary => Info is null
        ? string.Empty
        : $"{Info.Container} · {TimeSpan.FromSeconds(Info.DurationSeconds):hh\\:mm\\:ss} · {Info.Tracks.Count} track(s) · " +
          $"{Info.Attachments.Count} attachment(s){(Info.ChapterEntries > 0 ? $" · {Info.ChapterEntries} chapter(s)" : string.Empty)}";

    public event EventHandler<ToastRequest>? ToastRequested;

    public async Task OpenAsync(string path)
    {
        if (Settings.MkvToolNix is not { } tools)
        {
            Status = "mkvtoolnix is required: install it or set its folder in Settings.";
            return;
        }

        IsBusy = true;
        Items.Clear();
        Info = null;
        Source = path;
        Status = "Reading tracks…";
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
                    Kind = $"#{track.Id} {track.Type.ToString().ToLowerInvariant()}",
                    Title = track.Name.Length > 0 ? track.Name : track.Codec,
                    Details = string.Join(" · ", new[] { track.Codec, track.Language, track.Details,
                        track.Default ? "default" : null, track.Forced ? "forced" : null }.Where(s => !string.IsNullOrEmpty(s))),
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
                    Kind = $"attachment {attachment.Id}",
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
            Status = "Could not read the file: " + ex.Message;
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
            Status = "Nothing selected.";
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
                Status = $"Remuxing {info.Container} to Matroska first…";
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

            Status = "Extracting…";
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
                Status = $"Extracted {tracks.Count} track(s), {attachments.Count} attachment(s) to {OutputDirectory}" +
                         (result.Warnings.Count > 0 ? $" ({result.Warnings.Count} warning(s))." : ".");
                ToastRequested?.Invoke(this, new ToastRequest(ToastKind.Success, "Extraction finished."));
            }
            else
            {
                Status = "Extraction failed: " + string.Join("; ", result.Errors);
                ToastRequested?.Invoke(this, new ToastRequest(ToastKind.Error, Status));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Status = "Extraction failed: " + ex.Message;
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
