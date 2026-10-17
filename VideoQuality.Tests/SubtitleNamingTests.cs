using VideoQuality.Core.Mkv;

namespace VideoQuality.Tests;

public class SubtitleNamingTests
{
    [Theory]
    [InlineData("Silent Witch - 05 JPSC.ass", "zh-Hans")]
    [InlineData("[Group] Show [05][CHS].ass", "zh-Hans")]
    [InlineData("show.05.sc.ass", "zh-Hans")]
    [InlineData("第05话 简体.ass", "zh-Hans")]
    [InlineData("Silent Witch - 05 JPTC.ass", "zh-Hant")]
    [InlineData("[Group] Show [05][CHT].ass", "zh-Hant")]
    [InlineData("第05話 繁體.ass", "zh-Hant")]
    [InlineData("show_05_jp.ass", "ja")]
    [InlineData("show 05 eng.ass", "en")]
    public void Conventions_map_to_IETF_languages(string file, string language) =>
        Assert.Equal(language, SubtitleNaming.Guess(file)?.Language);

    [Theory]
    [InlineData("Scene 05.ass")]      // "Scene" must not read as "SC"
    [InlineData("Ep05 Tactics.ass")]  // nor "Tactics" as "TC"
    [InlineData("subs.ass")]
    public void Unrelated_names_give_no_guess(string file) => Assert.Null(SubtitleNaming.Guess(file));
}
