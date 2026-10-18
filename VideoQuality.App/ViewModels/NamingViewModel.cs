using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VideoQuality.Core.Localization;
using VideoQuality.Core.Media;
using VideoQuality.Core.Release;

namespace VideoQuality.App.ViewModels;

/// <summary>A name the Naming page produced, with what it is for.</summary>
public sealed record NamedFile(string Purpose, string Name);

/// <summary>Naming page: release file names and titles following the group's naming rules.</summary>
public partial class NamingViewModel : ViewModelBase
{
    private readonly Func<string, MediaInfo> _read;

    public NamingViewModel(Func<string, MediaInfo>? read = null)
    {
        _read = read ?? MediaInfoReader.Read;
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not (nameof(Status) or nameof(FileName) or nameof(ReleaseTitle)))
            {
                Update();
            }
        };
        Update();
    }

    public IReadOnlyList<EpisodeKind> Kinds { get; } = Enum.GetValues<EpisodeKind>();

    public IReadOnlyList<string> Sources { get; } = ["WebRip", "BDRip", "Web-DL", "DVDRip"];

    public IReadOnlyList<string> AudioCodecs { get; } = ["AAC", "FLAC"];

    public IReadOnlyList<SubtitleSet> SubtitleSets { get; } = Enum.GetValues<SubtitleSet>();

    public ObservableCollection<NamedFile> Files { get; } = [];

    [ObservableProperty] public partial string Group { get; set; } = ReleaseName.DefaultGroup;
    [ObservableProperty] public partial string Collaborators { get; set; } = string.Empty;
    [ObservableProperty] public partial string Title { get; set; } = string.Empty;
    [ObservableProperty] public partial string ChineseGroup { get; set; } = "绿茶字幕组";
    [ObservableProperty] public partial string ChineseTitle { get; set; } = string.Empty;
    [ObservableProperty] public partial EpisodeKind Kind { get; set; }
    [ObservableProperty] public partial decimal Episode { get; set; } = 1;
    [ObservableProperty] public partial decimal Version { get; set; } = 1;
    [ObservableProperty] public partial string Source { get; set; } = "WebRip";
    [ObservableProperty] public partial string VideoCodec { get; set; } = "HEVC-10bit";
    [ObservableProperty] public partial string Resolution { get; set; } = "1080p";
    [ObservableProperty] public partial string AudioCodec { get; set; } = "AAC";
    [ObservableProperty] public partial SubtitleSet Subtitles { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = Loc.T("naming.status.start");

    /// <summary>The 内封 MKV name.</summary>
    [ObservableProperty] public partial string FileName { get; set; } = string.Empty;

    [ObservableProperty] public partial string ReleaseTitle { get; set; } = string.Empty;

    public event EventHandler<string>? CopyRequested;

    public ReleaseName Name => new()
    {
        Group = Group.Trim().Length > 0 ? Group.Trim() : ReleaseName.DefaultGroup,
        Collaborators = Collaborators.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        Title = Title.Trim(),
        Kind = Kind,
        Episode = (int)Episode,
        Version = (int)Version,
        Source = Source,
        VideoCodec = VideoCodec,
        Resolution = Resolution,
        AudioCodec = AudioCodec,
        Subtitles = Subtitles,
    };

    /// <summary>
    /// Every file of the release: web sources get the 内封 MKV plus a simplified and a traditional MP4;
    /// BD sources the MKV (FLAC) and the two MP4s with AAC.
    /// </summary>
    public IReadOnlyList<ReleaseName> Set()
    {
        ReleaseName name = Name;
        if (!name.Source.StartsWith("BD", StringComparison.OrdinalIgnoreCase))
        {
            return name.WebSet();
        }

        IReadOnlyList<ReleaseName> burned = (name with { AudioCodec = "AAC" }).WebSet();
        return [name with { Delivery = SubtitleDelivery.Embedded, AudioCodec = "FLAC" }, burned[1], burned[2]];
    }

    /// <summary>Fills the form from a release file name, or from a video's properties.</summary>
    public async Task FillFromAsync(string path)
    {
        if (ReleaseName.Parse(path) is { } parsed)
        {
            Group = parsed.Group;
            Collaborators = string.Join('&', parsed.Collaborators);
            Title = parsed.Title;
            Kind = parsed.Kind;
            Episode = parsed.Episode;
            Version = parsed.Version;
            Source = parsed.Source;
            VideoCodec = parsed.VideoCodec;
            Resolution = parsed.Resolution;
            AudioCodec = parsed.AudioCodec;
            Subtitles = parsed.Subtitles;
            Status = Loc.T("naming.status.parsed");
            return;
        }

        try
        {
            MediaInfo info = await Task.Run(() => _read(path));
            if (info.Video is { } video)
            {
                VideoCodec = video.Codec switch
                {
                    "hevc" => $"HEVC-{video.BitDepth}bit",
                    "h264" => video.BitDepth > 8 ? $"AVC-{video.BitDepth}bit" : "AVC",
                    "av1" => $"AV1-{video.BitDepth}bit",
                    _ => video.Codec.ToUpperInvariant(),
                };
                Resolution = video.Height + "p";
            }

            if (info.AudioStreams.FirstOrDefault() is { } audio)
            {
                AudioCodec = audio.Codec.ToUpperInvariant();
            }

            Status = Loc.T("naming.status.fromVideo");
        }
        catch (InvalidOperationException ex)
        {
            Status = ex.Message;
        }
    }

    [RelayCommand]
    private void Copy(string? text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            CopyRequested?.Invoke(this, text);
        }
    }

    [RelayCommand]
    private void CopyAll() => CopyRequested?.Invoke(this, string.Join(Environment.NewLine, Files.Select(f => f.Name).Append(ReleaseTitle)));

    /// <inheritdoc />
    public override void RefreshTexts()
    {
        Update();
        base.RefreshTexts();
    }

    private void Update()
    {
        IReadOnlyList<ReleaseName> set = Set();
        Files.Clear();
        Files.Add(new NamedFile(Loc.T("naming.purpose.embedded"), set[0].FileName));
        Files.Add(new NamedFile(Loc.T("naming.purpose.burned", set[1].LanguageTag), set[1].FileName));
        Files.Add(new NamedFile(Loc.T("naming.purpose.burned", set[2].LanguageTag), set[2].FileName));
        FileName = set[0].FileName;
        ReleaseTitle = set[0].ReleaseTitle(ChineseGroup, ChineseTitle);
    }
}
