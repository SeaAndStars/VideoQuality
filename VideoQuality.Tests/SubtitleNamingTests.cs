using VideoQuality.Core.Mkv;

namespace VideoQuality.Tests;

public class SubtitleNamingTests
{
    [Theory]
    [InlineData("Silent Witch - 05 JPSC.ass", "zh", "JPSC")]
    [InlineData("Silent Witch - 05 JPTC.ass", "zh", "JPTC")]
    [InlineData("[Group] Show [05][CHS].ass", "zh", "SC")]
    [InlineData("show.05.sc.ass", "zh", "SC")]
    [InlineData("show.05.TC.ass", "zh", "TC")]
    [InlineData("show [BIG5].ass", "zh", "TC")]
    [InlineData("第05话 简体.ass", "zh", "SC")]
    [InlineData("第05话 简日双语.ass", "zh", "JPSC")]
    [InlineData("第05话 繁日双语.ass", "zh", "JPTC")]
    [InlineData("show.SCJP.ass", "zh", "JPSC")]
    [InlineData("show.05.jp.ass", "ja", "")]
    [InlineData("show.05.eng.ass", "en", "")]
    public void GreenTea_profile_follows_the_track_naming_rules(string file, string language, string name)
    {
        Assert.Equal((language, name), SubtitleNaming.Guess(file));
    }

    [Theory]
    [InlineData("Silent Witch - 05 JPSC.ass", "zh-Hans", "简体中文")]
    [InlineData("show.CHT.ass", "zh-Hant", "繁體中文")]
    [InlineData("show.05.jp.ass", "ja", "日本語")]
    [InlineData("show.05.english.ass", "en", "English")]
    public void Generic_profile_uses_script_subtags_and_native_names(string file, string language, string name)
    {
        Assert.Equal((language, name), SubtitleNaming.Guess(file, TrackNamingProfile.Generic));
    }

    [Theory]
    [InlineData("episode05.ass")]
    [InlineData("subs.ass")]
    public void Unrelated_names_give_no_guess(string file) => Assert.Null(SubtitleNaming.Guess(file));
}
