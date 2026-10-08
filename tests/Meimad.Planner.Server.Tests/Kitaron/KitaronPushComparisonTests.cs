using Meimad.Planner.Server.Application.Kitaron.Push;

namespace Meimad.Planner.Server.Tests.Kitaron;

public sealed class KitaronPushComparisonTests
{
    private static KitaronOperationRow Row(object? value) => new(1, 42, "010", false,
        new Dictionary<string, object?> { ["OperationQty"] = value });

    [Fact]
    public void Comparison_preserves_null_decimal_precision_and_datetime_ticks()
    {
        Assert.True(KitaronPushComparison.Matches(Row(null), Row(DBNull.Value)));
        Assert.False(KitaronPushComparison.Matches(Row(null), Row(0m)));
        Assert.True(KitaronPushComparison.Matches(Row(1.20m), Row(1.200m)));
        Assert.False(KitaronPushComparison.Matches(Row(1.20m), Row(1.201m)));
        Assert.False(KitaronPushComparison.Matches(Row(1d), Row(1.001d)));
        var date = new DateTime(2026, 10, 8, 9, 0, 0);
        Assert.False(KitaronPushComparison.Matches(Row(date), Row(date.AddMilliseconds(3))));
    }

    [Fact]
    public void Missing_closed_reassigned_and_renumbered_rows_conflict()
    {
        var expected = Row(12m);
        Assert.False(KitaronPushComparison.Matches(expected, null));
        Assert.False(KitaronPushComparison.Matches(expected, expected with { WorkOrderClosed = true }));
        Assert.False(KitaronPushComparison.Matches(expected, expected with { WorkOrderNumber = 43 }));
        Assert.False(KitaronPushComparison.Matches(expected, expected with { ActionNumber = "20" }));
        Assert.False(KitaronPushComparison.Matches(expected, expected with { Values = new Dictionary<string, object?>() }));
        Assert.True(KitaronPushComparison.Matches(expected, expected));
    }

    [Fact]
    public void Preview_stamp_guards_ERP_values_and_settings_without_freezing_dynamic_forecasts()
    {
        var settings = new KitaronPushSettings(false, 15, [], 1, DateTimeOffset.UtcNow, "planner");
        var write = new KitaronPushWrite(1, 42, "OperationQty", 20d, Row(12m));
        var plan = new KitaronPushPlanner.Plan([write], [], 1, 0, []);
        var stamp = KitaronPushComparison.Stamp(settings, plan);
        Assert.Equal(stamp, KitaronPushComparison.Stamp(settings, plan));
        Assert.NotEqual(stamp, KitaronPushComparison.Stamp(settings with { Version = 2 }, plan));
        // Push now recalculates current Planner values; a moving forecast is not an ERP edit.
        Assert.Equal(stamp, KitaronPushComparison.Stamp(settings, plan with { Writes = [write with { Value = 21d }] }));
        Assert.NotEqual(stamp, KitaronPushComparison.Stamp(settings, plan with { Writes = [write with { Expected = Row(13m) }] }));
    }
}
