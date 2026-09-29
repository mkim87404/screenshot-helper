using ScreenshotHelper.Core.Naming;

namespace ScreenshotHelper.Core.Tests;

public class ShotNameTests
{
    [Theory]
    [InlineData("5.png", 5, null, "")]
    [InlineData("5-3.png", 5, 3, "")]
    [InlineData("12-10 (2026-09-27 10.05.33 UTC+13) (login page).png", 12, 10, " (2026-09-27 10.05.33 UTC+13) (login page)")]
    [InlineData("3-1(blah).png", 3, 1, "(blah)")]
    [InlineData("5(blah).PNG", 5, null, "(blah)")]
    [InlineData("0-0.png", 0, 0, "")]
    public void Parses_numbering_and_keeps_tail(string fileName, int main, int? sub, string tail)
    {
        var name = ShotName.TryParse(fileName);

        Assert.NotNull(name);
        Assert.Equal(main, name.Main);
        Assert.Equal(sub, name.Sub);
        Assert.Equal(tail, name.Tail);
        Assert.Equal(fileName, name.FileName);
    }

    [Theory]
    [InlineData("20250101_123456.png")]
    [InlineData("Screenshot 2026-09-27.png")]
    [InlineData("5-1-2.png")]
    [InlineData("5-.png")]
    [InlineData("5.jpg")]
    [InlineData("5a.png")]
    [InlineData("99999999999.png")]
    [InlineData("٥.png")]
    public void Treats_foreign_names_as_not_ours(string fileName) => Assert.Null(ShotName.TryParse(fileName));

    [Theory]
    [InlineData("3-1(blah).png", 1, 1, "1-1(blah).png")]
    [InlineData("5 (a) (b).png", 5, 1, "5-1 (a) (b).png")]
    [InlineData("5-1 (x).png", 5, null, "5 (x).png")]
    [InlineData("05-02.png", 3, 4, "03-04.png")]
    [InlineData("12-3.png", 3, 4, "3-4.png")]
    [InlineData("7.PNG", 8, null, "8.PNG")]
    public void Renumbering_preserves_tail_extension_and_padding(string original, int main, int? sub, string expected)
    {
        var renamed = ShotName.TryParse(original)!.WithNumbers(main, sub);

        Assert.Equal(expected, renamed.FileName);
    }
}

public class ShotTailTests
{
    [Theory]
    [InlineData(13, 0, "2026-09-27 10.05.33 UTC+13")]
    [InlineData(5, 30, "2026-09-27 10.05.33 UTC+5.30")]
    [InlineData(-3, -30, "2026-09-27 10.05.33 UTC-3.30")]
    [InlineData(0, 0, "2026-09-27 10.05.33 UTC+0")]
    [InlineData(12, 45, "2026-09-27 10.05.33 UTC+12.45")]
    public void Local_timestamps_include_the_actual_offset(int hours, int minutes, string expected)
    {
        var at = new DateTimeOffset(2026, 9, 27, 10, 5, 33, 999, new TimeSpan(hours, minutes, 0));

        Assert.Equal(expected, ShotTail.FormatTimestamp(at, TimestampZone.Local));
    }

    [Fact]
    public void Utc_timestamps_convert_and_have_no_milliseconds()
    {
        var at = new DateTimeOffset(2026, 9, 27, 10, 5, 33, 750, TimeSpan.FromHours(13));

        Assert.Equal("2026-09-26 21.05.33 UTC", ShotTail.FormatTimestamp(at, TimestampZone.Utc));
    }

    [Fact]
    public void Longest_timestamp_matches_the_budget_constant()
    {
        var longest = ShotTail.FormatTimestamp(new DateTimeOffset(2026, 12, 31, 23, 59, 59, new TimeSpan(12, 45, 0)), TimestampZone.Local);

        Assert.Equal(ShotTail.MaxTimestampLength, longest.Length);
    }

    [Theory]
    [InlineData(null, null, "")]
    [InlineData("T", null, " (T)")]
    [InlineData(null, "C", " (C)")]
    [InlineData("T", "C", " (T) (C)")]
    public void Tail_is_timestamp_then_caption_each_in_brackets(string? timestamp, string? caption, string expected) =>
        Assert.Equal(expected, ShotTail.Build(timestamp, caption));

    [Fact]
    public void Timestamps_never_contain_invalid_filename_characters()
    {
        var text = ShotTail.FormatTimestamp(DateTimeOffset.Now, TimestampZone.Local);

        Assert.All(text, c => Assert.True(CaptionRules.IsAllowed(c)));
    }
}

public class CaptionRulesTests
{
    [Theory]
    [InlineData("  login page  ", "login page")]
    [InlineData("a<b>c:d\"e/f\\g|h?i*j", "abcdefghij")]
    [InlineData("   ", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("tab\there", "tabhere")]
    [InlineData("(nested) brackets", "(nested) brackets")]
    public void Normalize_trims_and_strips_invalid_characters(string? input, string? expected) =>
        Assert.Equal(expected, CaptionRules.Normalize(input));

    [Fact]
    public void Budget_shrinks_as_the_folder_path_grows_and_respects_max_path()
    {
        var shortFolder = @"C:\s";
        var longFolder = @"C:\" + new string('x', 150);

        var shortBudget = CaptionRules.Budget(shortFolder, 5);
        var longBudget = CaptionRules.Budget(longFolder, 5);

        Assert.True(longBudget < shortBudget);
        var worstName = new string('9', 5) + " (" + new string('t', ShotTail.MaxTimestampLength) + ") (" + new string('c', longBudget) + ").png";
        Assert.True(longFolder.Length + 1 + worstName.Length + CaptionRules.GrowthReserve <= CaptionRules.MaxPathLength);
    }

    [Fact]
    public void Budget_is_never_negative() => Assert.Equal(0, CaptionRules.Budget(@"C:\" + new string('x', 300), 5));

    [Fact]
    public void FitToPath_truncates_without_splitting_surrogate_pairs()
    {
        var folder = @"C:\" + new string('f', 240);
        var caption = new string('a', 5) + "😀😀😀";

        var fitted = CaptionRules.FitToPath(folder, "1.png", caption);

        Assert.NotNull(fitted);
        Assert.False(char.IsHighSurrogate(fitted[^1]));
        Assert.True(folder.Length + 1 + "1 ().png".Length + fitted.Length <= CaptionRules.MaxPathLength);
    }
}
