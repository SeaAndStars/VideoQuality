using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentIcons.Common;
using VideoQuality.Core.Localization;
using VideoQuality.Core.Media;
using VideoQuality.Core.Release;

namespace VideoQuality.App.ViewModels;

/// <summary>One line of the media report: a heading row or a stream row.</summary>
public sealed record MediaRow(Symbol Icon, string Title, string Details, bool IsHeading);

/// <summary>One line of the compliance check.</summary>
public sealed record FindingRow(FindingLevel Level, string Message)
{
    public bool IsPass => Level == FindingLevel.Pass;

    public bool IsWarning => Level == FindingLevel.Warning;

    public bool IsFail => Level == FindingLevel.Fail;
}

/// <summary>
/// Media Info page: what a file contains (like MediaInfo / HandBrake's summary) and whether a
/// finished release follows the group's rules.
/// </summary>
public partial class MediaInfoViewModel : ViewModelBase
{
    private readonly Func<string, MediaInfo> _read;

    public MediaInfoViewModel(Func<string, MediaInfo>? read = null) => _read = read ?? MediaInfoReader.Read;

    public ObservableCollection<MediaRow> Rows { get; } = [];

    public ObservableCollection<FindingRow> Findings { get; } = [];

    public IReadOnlyList<ReleaseProfile> Profiles { get; } = Enum.GetValues<ReleaseProfile>();

    public IReadOnlyList<SubtitleDelivery> Deliveries { get; } = Enum.GetValues<SubtitleDelivery>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInfo))]
    public partial MediaInfo? Info { get; set; }

    [ObservableProperty]
    public partial string Path { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Status { get; set; } = Loc.T("mediainfo.status.start");

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial ReleaseProfile Profile { get; set; }

    [ObservableProperty]
    public partial SubtitleDelivery Delivery { get; set; }

    [ObservableProperty]
    public partial string Verdict { get; set; } = string.Empty;

    public bool HasInfo => Info is not null;

    /// <summary>Plain-text report for the clipboard.</summary>
    public string Text => Info?.ToText() ?? string.Empty;

    public event EventHandler<string>? CopyRequested;

    public async Task OpenAsync(string path)
    {
        IsBusy = true;
        Path = path;
        Status = Loc.T("mediainfo.status.reading");
        try
        {
            MediaInfo info = await Task.Run(() => _read(path));
            Info = info;
            ReleaseName? name = ReleaseName.Parse(path);
            Delivery = name?.Delivery ?? (info.IsMp4 ? SubtitleDelivery.Burned : SubtitleDelivery.Embedded);
            bool bd = name?.Source.StartsWith("BD", StringComparison.OrdinalIgnoreCase) == true;
            bool movie = name?.Kind == EpisodeKind.Movie || info.Duration > TimeSpan.FromMinutes(70);
            Profile = (bd, movie) switch
            {
                (true, true) => ReleaseProfile.BdMovie,
                (true, false) => ReleaseProfile.BdEpisode,
                (false, true) => ReleaseProfile.WebMovie,
                _ => ReleaseProfile.WebEpisode,
            };
            BuildRows(info);
            Check();
            Status = Loc.T("mediainfo.status.done", info.Streams.Count, MediaInfo.FormatClock(info.Duration));
        }
        catch (InvalidOperationException ex)
        {
            Info = null;
            Rows.Clear();
            Findings.Clear();
            Verdict = string.Empty;
            Status = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnProfileChanged(ReleaseProfile value) => Check();

    partial void OnDeliveryChanged(SubtitleDelivery value) => Check();

    [RelayCommand]
    private void Copy() => CopyRequested?.Invoke(this, Text);

    /// <summary>Runs the group's rules against the file.</summary>
    public void Check()
    {
        Findings.Clear();
        if (Info is not { } info)
        {
            Verdict = string.Empty;
            return;
        }

        foreach (SpecFinding finding in GreenTeaSpec.Check(info, Profile, Delivery))
        {
            Findings.Add(new FindingRow(finding.Level, finding.Message));
        }

        int fails = Findings.Count(f => f.IsFail);
        int warnings = Findings.Count(f => f.IsWarning);
        Verdict = fails > 0 ? Loc.T("mediainfo.verdict.fail", fails, warnings)
            : warnings > 0 ? Loc.T("mediainfo.verdict.warn", warnings)
            : Loc.T("mediainfo.verdict.pass");
    }

    private void BuildRows(MediaInfo info)
    {
        Rows.Clear();
        Rows.Add(new MediaRow(Symbol.Document, System.IO.Path.GetFileName(info.Path),
            Loc.T("media.text.format", info.FormatLongName.Length > 0 ? info.FormatLongName : info.Format,
                MediaInfo.FormatClock(info.Duration), MediaInfo.FormatSize(info.Size), MediaInfo.FormatKbps(info.BitRate)), true));
        foreach (MediaStream stream in info.Streams)
        {
            Symbol icon = stream.Kind switch
            {
                StreamKind.Video => Symbol.Video,
                StreamKind.Audio => Symbol.MusicNote2,
                StreamKind.Subtitle => Symbol.ClosedCaption,
                StreamKind.Attachment => Symbol.TextFont,
                _ => Symbol.Document,
            };
            string title = $"#{stream.Index} {Loc.T("mediainfo.kind." + stream.Kind.ToString().ToLowerInvariant())}";
            var details = new List<string> { MediaInfo.Describe(stream) };
            if (stream.Language.Length > 0)
            {
                details.Add(stream.Language);
            }

            if (stream.Title.Length > 0)
            {
                details.Add("“" + stream.Title + "”");
            }

            if (stream.Default)
            {
                details.Add(Loc.T("track.flag.default"));
            }

            if (stream.Forced)
            {
                details.Add(Loc.T("track.flag.forced"));
            }

            Rows.Add(new MediaRow(icon, title, string.Join(" · ", details), false));
        }

        if (info.Chapters.Count > 0)
        {
            Rows.Add(new MediaRow(Symbol.BookOpen, Loc.T("mediainfo.chapters", info.Chapters.Count),
                string.Join("  ", info.Chapters.Select(c => $"{MediaInfo.FormatClock(c.Start)} {c.Title}")), true));
        }

        OnPropertyChanged(nameof(Text));
    }
}
