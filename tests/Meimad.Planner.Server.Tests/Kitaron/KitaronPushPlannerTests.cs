using Meimad.Planner.Server.Application.Kitaron.Push;

namespace Meimad.Planner.Server.Tests.Kitaron;

public sealed class KitaronPushPlannerTests
{
    private static readonly TimeZoneInfo Israel = TimeZoneInfo.FindSystemTimeZoneById("Asia/Jerusalem");

    private static readonly KitaronPushMapping[] AllFour =
    [
        new("OperationQty", "good_quantity", true),
        new("StartDateReal", "actual_start", true),
        new("FinishDateCalc", "forecast_finish", true),
        new("SetupTimeReal", "setup_minutes", true)
    ];

    [Fact]
    public void Planner_values_replace_different_Kitaron_values_in_factory_local_time()
    {
        var operation = Operation(
            start: DateTimeOffset.Parse("2026-09-28T05:10:30.4567Z"),
            qcPass: DateTimeOffset.Parse("2026-09-28T07:25:30.4567Z"),
            good: 12);
        var forecasts = new Dictionary<string, KitaronPushForecast>
        {
            ["op-1"] = new(DateTimeOffset.Parse("2026-09-28T05:00:00Z"), DateTimeOffset.Parse("2026-09-29T13:45:00Z"))
        };
        // Kitaron's own report set an earlier start; the Planner's value replaces it (owner decision).
        var row = Row(41043, "10", false, new()
        {
            ["StartDateReal"] = new DateTime(2026, 9, 28, 7, 0, 0),
            ["OperationQty"] = null,
            ["FinishDateCalc"] = null,
            ["SetupTimeReal"] = null
        });

        var plan = KitaronPushPlanner.Build(AllFour, [operation], forecasts, [row], Israel);

        Assert.Equal(1, plan.OperationsMatched);
        Assert.Equal(0, plan.OperationsSkipped);
        var writes = plan.Writes.ToDictionary(write => write.Column, write => write.Value);
        Assert.Equal(12.0, writes["OperationQty"]);
        Assert.Equal(new DateTime(2026, 9, 28, 8, 10, 30), writes["StartDateReal"]);      // UTC+3, whole seconds
        Assert.Equal(new DateTime(2026, 9, 29, 16, 45, 0), writes["FinishDateCalc"]);
        Assert.Equal(135.0, writes["SetupTimeReal"]);                                      // start to QC PASS, minutes
        var start = plan.Changes.Single(change => change.KitaronColumn == "StartDateReal");
        Assert.Equal("2026-09-28 07:00:00", start.OldValue);
        Assert.Equal("2026-09-28 08:10:30", start.NewValue);
        Assert.Equal("PN-1", start.PartNumber);
        Assert.All(plan.Writes, write => Assert.Equal(900L, write.RowId));
    }

    [Fact]
    public void Equal_values_are_not_rewritten_and_missing_Planner_values_never_clear_Kitaron()
    {
        var operation = Operation(start: DateTimeOffset.Parse("2026-09-28T05:10:30.9Z"), qcPass: null, good: 0);
        var row = Row(41043, " 010 ", false, new()
        {
            ["StartDateReal"] = new DateTime(2026, 9, 28, 8, 10, 30, 3),   // Kitaron's 3 ms rounding
            ["OperationQty"] = 7.0,
            ["SetupTimeReal"] = 44.0,
            ["FinishDateCalc"] = new DateTime(2026, 10, 1)
        });

        var plan = KitaronPushPlanner.Build(AllFour, [operation], new Dictionary<string, KitaronPushForecast>(), [row], Israel);

        Assert.Equal(1, plan.OperationsMatched);
        Assert.Empty(plan.Writes);
    }

    [Fact]
    public void Missing_ambiguous_and_closed_Kitaron_operations_are_left_alone_and_explained()
    {
        var operations = new[]
        {
            Operation(number: 41043, operationNumber: 10, good: 5),
            Operation(number: 41043, operationNumber: 20, good: 5, id: "op-2"),
            Operation(number: 41044, operationNumber: 10, good: 5, id: "op-3"),
            Operation(number: 41045, operationNumber: 10, good: 5, id: "op-4")
        };
        var rows = new[]
        {
            Row(41043, "10", false, new() { ["OperationQty"] = null }, rowId: 1),
            Row(41044, "10", false, new() { ["OperationQty"] = null }, rowId: 2),
            Row(41044, "10", false, new() { ["OperationQty"] = null }, rowId: 3),
            Row(41045, "10", true, new() { ["OperationQty"] = null }, rowId: 4)
        };

        var plan = KitaronPushPlanner.Build([new("OperationQty", "good_quantity", true)], operations,
            new Dictionary<string, KitaronPushForecast>(), rows, Israel);

        Assert.Equal(1, plan.OperationsMatched);
        Assert.Equal(3, plan.OperationsSkipped);
        Assert.Equal(1L, Assert.Single(plan.Writes).RowId);
        Assert.Contains(plan.Notes, note => note.StartsWith("1 operations have no Kitaron operation", StringComparison.Ordinal));
        Assert.Contains(plan.Notes, note => note.StartsWith("1 operations match more than one", StringComparison.Ordinal));
        Assert.Contains(plan.Notes, note => note.StartsWith("1 operations belong to Work Orders that are closed", StringComparison.Ordinal));
    }

    [Fact]
    public void Switched_off_columns_are_not_pushed()
    {
        var plan = KitaronPushPlanner.Build(
            [new("OperationQty", "good_quantity", false)], [Operation(good: 3)],
            new Dictionary<string, KitaronPushForecast>(), [Row(41043, "10", false, new() { ["OperationQty"] = null })],
            Israel);

        Assert.Empty(plan.Writes);
        Assert.Equal("No column is switched on, so nothing is pushed.", Assert.Single(plan.Notes));
    }

    private static KitaronPushOperation Operation(
        DateTimeOffset? start = null, DateTimeOffset? qcPass = null, int good = 0, int number = 41043,
        int operationNumber = 10, string id = "op-1") =>
        new(id, number, operationNumber, "PN-1", "Mill", start, null, qcPass, good, 20);

    private static KitaronOperationRow Row(
        int number, string actionNumber, bool closed, Dictionary<string, object?> values, long rowId = 900) =>
        new(rowId, number, actionNumber, closed, values);
}
