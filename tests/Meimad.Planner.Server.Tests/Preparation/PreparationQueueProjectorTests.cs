using Meimad.Planner.Server.Application.Preparation;
using Meimad.Planner.Server.Domain.Readiness;

namespace Meimad.Planner.Server.Tests.Preparation;

public sealed class PreparationQueueProjectorTests
{
    [Fact]
    public void Assigned_operation_without_current_compatible_nc_is_programming_pending()
    {
        var item = PreparationQueueProjector.Project(Source(Context(releases: [])));

        Assert.NotNull(item);
        Assert.Equal(PreparationQueueStages.ProgrammingPending, item.Stage);
        Assert.Contains(item.ReadinessFacts, fact => fact.Key == ReadinessComponentKeys.GCode && !fact.IsSatisfied);
    }

    [Fact]
    public void Nc_ready_operation_with_incomplete_exact_tool_readiness_is_tool_room_only()
    {
        var item = PreparationQueueProjector.Project(Source(Context(releases: [Release()])))!;

        Assert.Equal(PreparationQueueStages.ToolPreparationPending, item.Stage);
        Assert.Contains(item.ReadinessFacts, fact => fact.Key == ReadinessComponentKeys.ToolOffsets && !fact.IsSatisfied);
        Assert.NotEqual(PreparationQueueStages.SetupPending, item.Stage);
    }

    [Theory]
    [InlineData(null, "READY_FOR_SETUP")]
    [InlineData("OFFSET_LOADER_COMPLETED", "IN_SETUP")]
    [InlineData("SETUP_VERIFICATION_SUCCEEDED", "IN_SETUP_RUN")]
    public void Tool_ready_operation_is_setup_owned_and_preserves_workflow_projection(
        string? eventType,
        string expectedWorkflow)
    {
        var context = Context(
            releases: [Release()],
            offsets: [new("machine-1", "process-1", "gcode-1", "READY", null,
                DateTimeOffset.Parse("2026-09-01T06:00:00Z"))]);

        var item = PreparationQueueProjector.Project(Source(context, eventType, hasPackage: true))!;

        Assert.Equal(PreparationQueueStages.SetupPending, item.Stage);
        Assert.Equal(expectedWorkflow, item.WorkflowStatus);
        Assert.All(item.ReadinessFacts, fact => Assert.True(fact.IsSatisfied));
    }

    [Theory]
    [InlineData("SEND_TO_QC")]
    [InlineData("QC_PASS")]
    [InlineData("CYCLE_START")]
    public void Later_workflow_removes_operation_from_preparation_queues(string eventType)
    {
        var context = Context(
            releases: [Release()],
            offsets: [new("machine-1", "process-1", "gcode-1", "READY", null,
                DateTimeOffset.Parse("2026-09-01T06:00:00Z"))]);

        Assert.Null(PreparationQueueProjector.Project(Source(context, eventType)));
    }

    private static PreparationQueueSource Source(
        ProductionReadinessContext context,
        string? eventType = null,
        bool hasPackage = false) => new(
            "operation-1", "run-1", "assignment-1", "machine-1", "M01", "Mill",
            "PN-1", "Part", "B1", 10, "Rough", eventType, context, hasPackage);

    private static ProductionReadinessContext Context(
        IReadOnlyList<ReadinessRelease> releases,
        IReadOnlyList<ToolOffsetReadinessFact>? offsets = null) => new(
            "operation-1",
            "assignment-1",
            "machine-1",
            "CNC_GCODE",
            new HashSet<string>(["post-1"], StringComparer.Ordinal),
            20,
            "process-1",
            "tools-1",
            1,
            releases,
            null,
            offsets ?? [],
            "MISSING",
            null);

    private static ReadinessRelease Release() =>
        new("gcode-1", "process-1", "post-1", "Haas", "O1000.nc", 1);
}
