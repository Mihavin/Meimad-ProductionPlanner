using Meimad.Planner.Server.Domain.Readiness;

namespace Meimad.Planner.Server.Tests.GCode;

public sealed class ProductionReadinessEvaluatorTests
{
    [Fact]
    public void Manual_machine_does_not_require_gcode_but_keeps_other_requirements()
    {
        var context = ReadyContext() with
        {
            ExecutionMode = "MANUAL",
            Releases = [],
            SelectedGCodeReleaseId = null,
            ToolOffsetFacts = [Offset(gcodeId: null)]
        };

        var result = ProductionReadinessEvaluator.Evaluate(context);

        Assert.True(result.IsReadyForProduction);
        Assert.Equal(ReadinessStates.NotRequired, Component(result, "gcode").State);
        Assert.Equal(ReadinessStates.NotRequired,
            Component(result, "machinePostprocessorCompatibility").State);
        Assert.Equal(ReadinessStates.Ready, Component(result, "toolOffsets").State);
    }

    [Fact]
    public void Cnc_current_release_is_ready_and_stale_release_is_outdated()
    {
        var ready = ProductionReadinessEvaluator.Evaluate(ReadyContext());
        Assert.True(ready.IsReadyForProduction);
        Assert.Equal(ReadinessStates.Ready, Component(ready, "gcode").State);

        var stale = ProductionReadinessEvaluator.Evaluate(ReadyContext() with
        {
            Releases = [Release("release-old", "process-old", "post-a")],
            SelectedGCodeReleaseId = "release-old",
            ToolOffsetFacts = []
        });
        Assert.Equal(ReadinessStates.Outdated, Component(stale, "gcode").State);
        Assert.False(stale.IsReadyForProduction);
    }

    [Fact]
    public void Cnc_incompatible_and_multiple_current_releases_are_explainable()
    {
        var incompatible = ProductionReadinessEvaluator.Evaluate(ReadyContext() with
        {
            SupportedPostprocessorIds = new HashSet<string>(["post-b"]),
            SelectedGCodeReleaseId = null,
            ToolOffsetFacts = []
        });
        Assert.Equal(ReadinessStates.Incompatible, Component(incompatible, "gcode").State);
        Assert.Equal(ReadinessStates.Incompatible,
            Component(incompatible, "machinePostprocessorCompatibility").State);

        var multiple = ProductionReadinessEvaluator.Evaluate(ReadyContext() with
        {
            SupportedPostprocessorIds = new HashSet<string>(["post-a", "post-b"]),
            Releases =
            [
                Release("release-a", "process-1", "post-a"),
                Release("release-b", "process-1", "post-b")
            ],
            SelectedGCodeReleaseId = null,
            ToolOffsetFacts = []
        });
        Assert.True(multiple.RequiresExplicitGCodeSelection);
        Assert.Equal(ReadinessStates.Blocked, Component(multiple, "gcode").State);
        Assert.Contains("select", Component(multiple, "gcode").Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Missing_table_offsets_material_and_capacity_each_block_overall_readiness()
    {
        var missingTable = ProductionReadinessEvaluator.Evaluate(ReadyContext() with
        {
            ActiveToolTableReleaseId = null,
            Releases = [],
            SelectedGCodeReleaseId = null,
            ToolOffsetFacts = []
        });
        Assert.Equal(ReadinessStates.Missing, Component(missingTable, "toolTable").State);

        var missingOffsets = ProductionReadinessEvaluator.Evaluate(ReadyContext() with
        {
            ToolOffsetFacts = []
        });
        Assert.Equal(ReadinessStates.Missing, Component(missingOffsets, "toolOffsets").State);

        var material = ProductionReadinessEvaluator.Evaluate(ReadyContext() with
        {
            MaterialStatus = ReadinessStates.Unverified
        });
        Assert.Equal(ReadinessStates.Unverified, Component(material, "material").State);

        var capacity = ProductionReadinessEvaluator.Evaluate(ReadyContext() with
        {
            RequiredToolCount = 25,
            UsableToolPositions = 20
        });
        Assert.Equal(ReadinessStates.Blocked, Component(capacity, "toolCapacity").State);
        Assert.Contains("requires 25", Component(capacity, "toolCapacity").Message);

        Assert.All([missingTable, missingOffsets, material, capacity],
            result => Assert.False(result.IsReadyForProduction));
    }

    [Fact]
    public void Machine_reassignment_immediately_changes_compatibility_capacity_and_offsets()
    {
        var reassigned = ProductionReadinessEvaluator.Evaluate(ReadyContext() with
        {
            MachineId = "machine-2",
            SupportedPostprocessorIds = new HashSet<string>(["post-b"]),
            UsableToolPositions = 0
        });

        Assert.Equal(ReadinessStates.Incompatible, Component(reassigned, "gcode").State);
        Assert.Equal(ReadinessStates.Blocked, Component(reassigned, "toolCapacity").State);
        Assert.Equal(ReadinessStates.Outdated, Component(reassigned, "toolOffsets").State);
    }

    [Fact]
    public void Legacy_operation_bypasses_release_requirements_but_not_batch_material()
    {
        var missing = ProductionReadinessEvaluator.Evaluate(ReadyContext() with
        {
            ActiveProcessRevisionId = null,
            ActiveToolTableReleaseId = null,
            Releases = [],
            SelectedGCodeReleaseId = null,
            ToolOffsetFacts = [],
            MaterialStatus = ReadinessStates.Missing
        });
        Assert.False(missing.IsManaged);
        Assert.False(missing.IsReadyForProduction);
        Assert.Equal(ReadinessStates.NotRequired, Component(missing, "gcode").State);
        Assert.Equal(ReadinessStates.Missing, Component(missing, "material").State);

        var ready = ProductionReadinessEvaluator.Evaluate(ReadyContext() with
        {
            ActiveProcessRevisionId = null,
            ActiveToolTableReleaseId = null,
            Releases = [],
            SelectedGCodeReleaseId = null,
            ToolOffsetFacts = []
        });
        Assert.False(ready.IsManaged);
        Assert.True(ready.IsReadyForProduction);
    }

    private static ProductionReadinessContext ReadyContext() => new(
        "batch-op-1", "assignment-1", "machine-1", "CNC_GCODE",
        new HashSet<string>(["post-a"]), 10,
        "process-1", "tools-1", 1,
        [Release("release-a", "process-1", "post-a")],
        "release-a", [Offset("release-a")], ReadinessStates.Ready,
        "Material physically verified");

    private static ReadinessRelease Release(
        string id, string processId, string postprocessorId) => new(
            id, processId, postprocessorId,
            postprocessorId == "post-a" ? "Post A" : "Post B",
            $"{id}.nc", 1);

    private static ToolOffsetReadinessFact Offset(string? gcodeId) => new(
        "machine-1", "process-1", gcodeId, ReadinessStates.Ready,
        "Offsets confirmed", DateTimeOffset.Parse("2026-08-20T00:00:00Z"));

    private static ReadinessComponent Component(
        ProductionReadinessResult result, string key) =>
        result.Components.Single(component => component.Key == key);
}
