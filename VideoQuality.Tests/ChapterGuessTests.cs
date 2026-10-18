using VideoQuality.Core.Chapters;

namespace VideoQuality.Tests;

public class ChapterGuessTests
{
    private static readonly TimeSpan Episode = TimeSpan.FromMinutes(24);

    private static ChapterMark Mark(double seconds, string name = "") => new(TimeSpan.FromSeconds(seconds), name);

    [Fact]
    public void Song_length_sections_are_the_opening_and_ending()
    {
        // Prologue, OP (90 s), part A, part B, ED (89 s), preview.
        ChapterMark[] marks = [Mark(0), Mark(120), Mark(210), Mark(800), Mark(1300), Mark(1389)];
        Assert.Equal((1, 4), ChapterList.GuessOpEd(marks, Episode));
        Assert.Equal(["Chapter1", "OP", "Chapter2", "Chapter3", "ED", "Chapter4"],
            ChapterList.AutoName(marks, 1, 4).Select(m => m.Name));
        Assert.Equal(((int?)null, (int?)null), ChapterList.GuessOpEd(marks, Episode, bySongLength: false));
    }

    [Fact]
    public void Names_win_over_lengths_and_a_last_song_runs_to_the_end()
    {
        ChapterMark[] named = [Mark(0, "Avant"), Mark(60, "opening"), Mark(600), Mark(1350, "Ending")];
        Assert.Equal((1, 3), ChapterList.GuessOpEd(named, Episode, bySongLength: false));
        ChapterMark[] tail = [Mark(0), Mark(1350)]; // the last mark runs 90 s to the end
        Assert.Equal(((int?)null, (int?)1), ChapterList.GuessOpEd(tail, Episode));
        Assert.Equal(((int?)null, (int?)null), ChapterList.GuessOpEd([], Episode));
    }

    [Fact]
    public void A_cut_keeps_the_running_mark_and_moves_the_rest()
    {
        ChapterMark[] marks = [Mark(0, "A"), Mark(120, "OP"), Mark(210, "B"), Mark(1300, "ED")];
        Assert.Equal([Mark(0, "OP"), Mark(30, "B")], ChapterList.Cut(marks, TimeSpan.FromSeconds(180), TimeSpan.FromSeconds(1300)));
        Assert.Equal([Mark(0, "A"), Mark(120, "OP")], ChapterList.Cut(marks, TimeSpan.Zero, TimeSpan.FromSeconds(200)));
    }
}
