using Meimad.Planner.Client.Windows.Formatting;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

public sealed class DurationTextTests
{
    [Theory]
    [InlineData(0, "00:00:00")]
    [InlineData(3723, "01:02:03")]
    [InlineData(90001, "25:00:01")]
    [InlineData(int.MaxValue, "596523:14:07")]
    public void Formats_seconds_with_total_hours(int seconds, string expected)
    {
        Assert.Equal(expected, DurationText.Format(seconds));
    }

    [Theory]
    [InlineData("00:00:00", 0)]
    [InlineData("01:02:03", 3723)]
    [InlineData("25:00:01", 90001)]
    [InlineData("596523:14:07", int.MaxValue)]
    public void Parses_total_hours_to_wire_seconds(string text, int expected)
    {
        Assert.True(DurationText.TryParseOptional(text, out var seconds));
        Assert.Equal(expected, seconds);
    }

    [Fact]
    public void Empty_duration_remains_null()
    {
        Assert.True(DurationText.TryParseOptional(string.Empty, out var seconds));
        Assert.Null(seconds);
    }

    [Theory]
    [InlineData("1:02:03")]
    [InlineData("01:60:00")]
    [InlineData("01:00:60")]
    [InlineData("-1:00:00")]
    [InlineData("596523:14:08")]
    [InlineData("999999999999999999999999:00:00")]
    public void Rejects_noncanonical_or_overflowing_duration(string text)
    {
        Assert.False(DurationText.TryParseOptional(text, out _));
    }
}
