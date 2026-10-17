using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VideoQuality.Core.Chapters;
using VideoQuality.Core.Localization;
using VideoQuality.Core.Media;

namespace VideoQuality.App.ViewModels;

/// <summary>One editable chapter.</summary>
public partial class ChapterRow : ObservableObject
{
    [ObservableProperty]
    public partial string Time { get; set; } = "00:00:00.000";

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsOpening { get; set; }

    [ObservableProperty]
    public partial bool IsEnding { get; set; }

    /// <summary>Null when <see cref="Time"/> is not a valid time.</summary>
    public TimeSpan? Start => PresetEditor.ParseTime(Time) is double seconds ? TimeSpan.FromSeconds(seconds) : null;
}

/// <summary>Chapters page: edit, import, name the fansub way, check and export chapter files.</summary>
public partial class ChaptersViewModel : ObservableObject
{
    private readonly Func<string, MediaInfo> _read;

    public ChaptersViewModel(Func<string, MediaInfo>? read = null)
    {
        _read = read ?? MediaInfoReader.Read;
        Rows.CollectionChanged += (_, e) =>
        {
            foreach (ChapterRow row in e.NewItems?.OfType<ChapterRow>() ?? [])
            {
                row.PropertyChanged += (_, _) => Check();
            }

            Check();
        };
    }

    public ObservableCollection<ChapterRow> Rows { get; } = [];

    public ObservableCollection<string> Problems { get; } = [];

    [ObservableProperty]
    public partial TimeSpan Duration { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = Loc.T("chapters.status.start");

    public bool HasRows => Rows.Count > 0;

    /// <summary>Raised with the saved chapter file when the user sends it to Mux.</summary>
    public event EventHandler<string>? SendToMuxRequested;

    /// <summary>Reads chapters from a video (its container) or a chapter file (.xml/.txt).</summary>
    public async Task ImportAsync(string path)
    {
        try
        {
            IReadOnlyList<ChapterMark> marks;
            if (Path.GetExtension(path).ToLowerInvariant() is ".xml" or ".txt")
            {
                marks = ChapterList.Load(path);
            }
            else
            {
                MediaInfo info = await Task.Run(() => _read(path));
                Duration = info.Duration;
                marks = ChapterList.FromMedia(info);
            }

            Load(marks);
            Status = Loc.T("chapters.status.imported", marks.Count, Path.GetFileName(path));
        }
        catch (Exception ex) // any unreadable file: say why, keep the page usable
        {
            Status = ex.Message;
        }
    }

    public void Load(IReadOnlyList<ChapterMark> marks)
    {
        Rows.Clear();
        foreach (ChapterMark mark in marks)
        {
            Rows.Add(new ChapterRow
            {
                Time = ChapterList.FormatTime(mark.Start),
                Name = mark.Name,
                IsOpening = mark.Name == "OP",
                IsEnding = mark.Name == "ED",
            });
        }

        Check();
    }

    public IReadOnlyList<ChapterMark> Marks() =>
        Rows.Select(r => new ChapterMark(r.Start ?? TimeSpan.Zero, r.Name.Trim())).ToList();

    [RelayCommand]
    private void Add()
    {
        TimeSpan after = Rows.LastOrDefault()?.Start is TimeSpan last ? last + TimeSpan.FromMinutes(1) : TimeSpan.Zero;
        Rows.Add(new ChapterRow { Time = ChapterList.FormatTime(after) });
    }

    [RelayCommand]
    private void Remove(ChapterRow row) => Rows.Remove(row);

    [RelayCommand]
    private void Clear() => Rows.Clear();

    /// <summary>OP/ED for the marked rows, Chapter1, Chapter2, … for the rest.</summary>
    [RelayCommand]
    private void AutoName()
    {
        int opening = Rows.ToList().FindIndex(r => r.IsOpening);
        int ending = Rows.ToList().FindIndex(r => r.IsEnding);
        IReadOnlyList<ChapterMark> named = ChapterList.AutoName(Marks(), opening >= 0 ? opening : null, ending >= 0 ? ending : null);
        for (int i = 0; i < Rows.Count; i++)
        {
            Rows[i].Name = named[i].Name;
        }
    }

    /// <summary>Sorts the rows by time.</summary>
    [RelayCommand]
    private void Sort()
    {
        List<ChapterRow> sorted = [.. Rows.OrderBy(r => r.Start ?? TimeSpan.MaxValue)];
        Rows.Clear();
        foreach (ChapterRow row in sorted)
        {
            Rows.Add(row);
        }
    }

    public void Save(string path)
    {
        ChapterList.Save(path, Marks());
        Status = Loc.T("chapters.status.saved", path);
    }

    /// <summary>Saves to a temporary XML file and hands it to the Mux page.</summary>
    [RelayCommand]
    private void SendToMux()
    {
        string path = Path.Combine(Path.GetTempPath(), "vq-chapters-" + Guid.NewGuid().ToString("N")[..8] + ".xml");
        Save(path);
        SendToMuxRequested?.Invoke(this, path);
    }

    private void Check()
    {
        Problems.Clear();
        if (Rows.Any(r => r.Start is null))
        {
            Problems.Add(Loc.T("chapters.check.badTime"));
        }

        if (Rows.Count > 0)
        {
            foreach (string problem in ChapterList.Check(Marks(), Duration))
            {
                Problems.Add(problem);
            }
        }

        OnPropertyChanged(nameof(HasRows));
    }
}
