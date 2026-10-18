using VideoQuality.Core.Release;

namespace VideoQuality.Tests;

public class SourceNameTests
{
    [Theory]
    [InlineData("Silent Witch - Chinmoku no Majo no Kakushigoto 05.mkv", "Silent Witch - Chinmoku no Majo no Kakushigoto", EpisodeKind.Episode, 5, 1, false, false)]
    [InlineData("[SubsPlease] Takopii no Genzai - 03v2 (1080p) [ABCD1234].mkv", "Takopii no Genzai", EpisodeKind.Episode, 3, 2, false, false)]
    [InlineData("Frieren S01E12 1080p WEB-DL AAC2.0 H.264.mkv", "Frieren", EpisodeKind.Episode, 12, 1, false, true)]
    [InlineData("Show S01E05v2.mkv", "Show", EpisodeKind.Episode, 5, 2, false, false)]
    [InlineData("葬送的芙莉莲 第05话.mp4", "葬送的芙莉莲", EpisodeKind.Episode, 5, 1, false, false)]
    [InlineData("[VCB-Studio] Bocchi the Rock! OVA 02 [Ma10p_1080p][BDRip].mkv", "Bocchi the Rock!", EpisodeKind.Ova, 2, 1, true, false)]
    [InlineData("Show OAD 1.mkv", "Show", EpisodeKind.Oad, 1, 1, false, false)]
    [InlineData("Show SP3v2.mkv", "Show", EpisodeKind.Special, 3, 2, false, false)]
    [InlineData("Sousou.no.Frieren.E07.1080p.WEBRip.x265.mkv", "Sousou no Frieren", EpisodeKind.Episode, 7, 1, false, true)]
    [InlineData("86 - Eighty Six 05 [BDRip 1080p].mkv", "86 - Eighty Six", EpisodeKind.Episode, 5, 1, true, false)]
    [InlineData("Kimi no Na wa The Movie [BD 1080p].mkv", "Kimi no Na wa", EpisodeKind.Movie, null, 1, true, false)]
    [InlineData("random.mkv", "random", EpisodeKind.Episode, null, 1, false, false)]
    public void Source_names_give_title_episode_and_origin(string file, string title, EpisodeKind kind, int? episode, int version, bool bd, bool web) =>
        Assert.Equal(new SourceName { Title = title, Kind = kind, Episode = episode, Version = version, Bd = bd, Web = web }, SourceName.Parse("/media/" + file));
}
