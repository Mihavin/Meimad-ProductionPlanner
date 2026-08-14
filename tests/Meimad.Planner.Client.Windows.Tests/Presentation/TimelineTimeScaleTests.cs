using System.Windows.Media;
using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Views;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

public sealed class TimelineTimeScaleTests
{
    [Theory]
    [InlineData("2026-08-14T05:59:00Z", false)]
    [InlineData("2026-08-14T06:00:00Z", true)]
    [InlineData("2026-08-14T17:59:00Z", true)]
    [InlineData("2026-08-14T18:00:00Z", false)]
    public void Classifies_configured_display_hours_at_boundaries(string value, bool expected)
    {
        Assert.Equal(
            expected,
            TimelineView.IsDaylightHour(
                DateTimeOffset.Parse(value),
                TimeZoneInfo.Utc,
                TimeSpan.FromHours(6),
                TimeSpan.FromHours(18)));
    }

    [Fact]
    public void Time_scale_palette_and_tooltips_distinguish_daylight_and_dark_hours()
    {
        var daylight = Assert.IsType<SolidColorBrush>(TimelineView.TimeScaleDaylightBrush);
        var dark = Assert.IsType<SolidColorBrush>(TimelineView.TimeScaleDarkBrush);

        Assert.NotEqual(daylight.Color, dark.Color);
        Assert.Contains("DAYLIGHT HOURS", TimelineView.TimeScaleToolTip(true));
        Assert.Contains("DARK HOURS", TimelineView.TimeScaleToolTip(false));
        var zone = TimeZoneInfo.CreateCustomTimeZone("Factory/+05", TimeSpan.FromHours(5), "Factory +05", "Factory +05");
        Assert.Contains(
            "outside configured DAY window",
            TimelineView.TimeScaleToolTip(
                false, zone, TimeSpan.FromHours(6), TimeSpan.FromHours(18)));
    }

    [Theory]
    [InlineData(8, 900, 1)]
    [InlineData(720, 6000, 3)]
    [InlineData(168, 3696, 2)]
    [InlineData(2000, 6000, 12)]
    [InlineData(20000, 6000, 168)]
    public void Chooses_bounded_hour_tick_density(double totalHours, double chartWidth, double expectedHours)
    {
        Assert.Equal(expectedHours, TimelineView.SelectHourTickHours(totalHours, chartWidth));
    }

    [Fact]
    public void Formats_hours_in_the_configured_factory_zone_without_os_timezone_dependency()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Factory/+05", TimeSpan.FromHours(5), "Factory +05", "Factory +05");

        Assert.Equal(
            "05",
            TimelineView.FormatTimeScaleHour(
                DateTimeOffset.Parse("2026-01-01T00:00:00Z"), zone, 1));
    }

    [Fact]
    public void Formats_dst_spring_skip_and_fall_repeat_with_offsets()
    {
        var zone = CreateEasternDstZone();

        Assert.Equal(
            "01",
            TimelineView.FormatTimeScaleHour(
                DateTimeOffset.Parse("2026-03-08T06:59:00Z"), zone, 1));
        Assert.Equal(
            "03",
            TimelineView.FormatTimeScaleHour(
                DateTimeOffset.Parse("2026-03-08T07:00:00Z"), zone, 1));

        var first = TimelineView.FormatTimeScaleHour(
            DateTimeOffset.Parse("2026-11-01T05:30:00Z"), zone, 1);
        var second = TimelineView.FormatTimeScaleHour(
            DateTimeOffset.Parse("2026-11-01T06:30:00Z"), zone, 1);
        Assert.Equal("01:30 -04:00", first);
        Assert.Equal("01:30 -05:00", second);
    }

    [Fact]
    public void Builds_clipped_contiguous_header_spans_without_timeline_identity()
    {
        var spans = TimelineView.BuildTimeScaleSpans(
            DateTimeOffset.Parse("2026-01-01T10:00:00Z"),
            DateTimeOffset.Parse("2026-01-03T10:00:00Z"),
            TimeZoneInfo.Utc,
            TimeSpan.FromHours(6),
            TimeSpan.FromHours(18));

        Assert.Equal(5, spans.Count);
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T10:00:00Z"), spans[0].Start);
        Assert.Equal(DateTimeOffset.Parse("2026-01-03T10:00:00Z"), spans[^1].End);
        Assert.All(spans, span => Assert.True(span.End > span.Start));
        Assert.DoesNotContain(
            spans.Zip(spans.Skip(1)),
            pair => pair.First.Daylight == pair.Second.Daylight);
        Assert.DoesNotContain(
            typeof(TimelineView.TimeScaleSpan).GetProperties(),
            property => property.PropertyType == typeof(TimelineInterval));
    }

    [Fact]
    public void Long_horizons_bound_header_render_plan_and_date_labels()
    {
        var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var end = start.AddHours(20_000);
        var zone = TimeZoneInfo.Utc;
        var plan = TimelineView.BuildTimeScaleRenderPlan(
            start, end, zone, TimeSpan.FromHours(6), TimeSpan.FromHours(18), 512);
        var dateBoundaries = TimelineView.LocalDateBoundaries(start, end, zone, 6000);

        Assert.InRange(plan.Count, 1, 512);
        Assert.Contains(plan, span => span.Daylight);
        Assert.Contains(plan, span => !span.Daylight);
        Assert.InRange(dateBoundaries.Count, 1, 100);
        Assert.All(
            dateBoundaries.Zip(dateBoundaries.Skip(1)),
            pair => Assert.True((pair.Second - pair.First).TotalDays >= 10));
    }

    [Theory]
    [InlineData(512)]
    [InlineData(1024)]
    public void Resolution_cap_preserves_exact_day_dark_cycles_for_long_horizons(int days)
    {
        var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var plan = TimelineView.BuildTimeScaleRenderPlan(
            start,
            start.AddDays(days),
            TimeZoneInfo.Utc,
            TimeSpan.FromHours(6),
            TimeSpan.FromHours(18),
            512,
            6000);

        Assert.Equal(days * 2 + 1, plan.Count);
        Assert.DoesNotContain(plan, span => span.IsMixed);
        Assert.Contains(plan, span => span.Daylight);
        Assert.Contains(plan, span => !span.Daylight);
    }

    [Fact]
    public void Compressed_bins_use_mixed_coverage_instead_of_midpoint_aliasing()
    {
        var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var plan = TimelineView.BuildTimeScaleRenderPlan(
            start,
            start.AddDays(100),
            TimeZoneInfo.Utc,
            TimeSpan.FromHours(6),
            TimeSpan.FromHours(18),
            16,
            8);

        Assert.InRange(plan.Count, 1, 16);
        Assert.Contains(plan, span => span.IsMixed);
        Assert.All(plan, span => Assert.True(span.IsMixed));
    }

    [Fact]
    public void High_span_compression_stays_within_resolution_cap()
    {
        var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var plan = TimelineView.BuildTimeScaleRenderPlan(
            start,
            start.AddDays(20_000),
            TimeZoneInfo.Utc,
            TimeSpan.FromHours(6),
            TimeSpan.FromHours(18),
            512,
            6000);

        Assert.InRange(plan.Count, 1, 12_000);
        Assert.Contains(plan, span => span.IsMixed);
    }

    [Fact]
    public void Normal_horizons_keep_daily_date_boundaries_and_day_dark_spans()
    {
        var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var end = start.AddDays(7);
        var plan = TimelineView.BuildTimeScaleRenderPlan(
            start, end, TimeZoneInfo.Utc, TimeSpan.FromHours(6), TimeSpan.FromHours(18), 512);
        var dateBoundaries = TimelineView.LocalDateBoundaries(start, end, TimeZoneInfo.Utc, 3696);

        Assert.Equal(6, dateBoundaries.Count);
        Assert.InRange(plan.Count, 10, 20);
        Assert.Contains(plan, span => span.Daylight);
        Assert.Contains(plan, span => !span.Daylight);
    }

    [Theory]
    [InlineData("2026-01-01T10:00:00Z", true)]
    [InlineData("2026-01-01T00:00:00Z", true)]
    [InlineData("2026-01-02T00:00:00Z", false)]
    public void Current_time_marker_uses_start_inclusive_end_exclusive_horizon(
        string value,
        bool expected)
    {
        var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var end = start.AddDays(1);

        Assert.Equal(
            expected,
            TimelineView.IsCurrentTimeWithinHorizon(DateTimeOffset.Parse(value), start, end));
    }

    [Fact]
    public void Current_time_marker_maps_to_absolute_timeline_geometry()
    {
        var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var end = start.AddHours(10);

        Assert.Equal(
            685,
            TimelineView.CurrentTimeMarkerX(start.AddHours(5), start, end, 185, 1000),
            precision: 6);
    }

    [Fact]
    public void Current_time_marker_label_uses_configured_factory_zone_and_has_text()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone(
            "Factory/+05",
            TimeSpan.FromHours(5),
            "Factory +05",
            "Factory +05");

        Assert.Equal(
            "NOW 2026-01-01 05:30 +05:00",
            TimelineView.CurrentTimeMarkerLabel(
                DateTimeOffset.Parse("2026-01-01T00:30:00Z"), zone));
        Assert.DoesNotContain(
            typeof(TimelineView.TimeScaleSpan).GetProperties(),
            property => property.PropertyType == typeof(TimelineInterval));
    }

    private static TimeZoneInfo CreateEasternDstZone()
    {
        var start = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
            new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday);
        var end = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
            new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2020, 1, 1),
            new DateTime(2030, 12, 31),
            TimeSpan.FromHours(1),
            start,
            end);
        return TimeZoneInfo.CreateCustomTimeZone(
            "Factory/Eastern",
            TimeSpan.FromHours(-5),
            "Factory Eastern",
            "Factory Eastern Standard",
            "Factory Eastern Daylight",
            [rule]);
    }
}
