using Meimad.Planner.Server.Domain.Readiness;

namespace Meimad.Planner.Server.Tests.GCode;

public sealed class ProductionActionPolicyTests
{
    private static ProductionReadinessContext Ready => new("operation", "assignment", "machine", "CNC_GCODE",
        new HashSet<string>(["post"]), 10, "process", "tools", 1,
        [new("nc", "process", "post", "Post", "part.nc", 1)], "nc", [], "READY", null,
        new("tools", 1, 1, 0, DateTimeOffset.UnixEpoch));

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public void Setup_requires_actual_loader_or_supported_manual_report(bool manual, bool loader, bool allowed)
    {
        var context = Ready with { MaterialStatus = "MISSING", ManualSetupReportingSupported = manual, LoaderExecutionObserved = loader };
        var decision = ProductionReadinessEvaluator.Evaluate(context).Actions!.Single(x => x.Action == "RecordSetupStart");
        Assert.Equal(allowed, decision.IsAllowed);
        Assert.Contains(decision.Reasons, x => x.Code == "material.missing" && x.Classification == "ATTENTION");
    }

    [Fact]
    public void Material_is_attention_for_planning_and_package_but_blocks_production()
    {
        var context = Ready with { MaterialStatus = "MISSING" };
        var result = ProductionReadinessEvaluator.Evaluate(context);
        Assert.True(result.Actions!.Single(x => x.Action == "Plan").IsAllowed);
        Assert.True(result.Actions!.Single(x => x.Action == "CreatePackage").IsAllowed);
        Assert.False(result.Actions!.Single(x => x.Action == "RunStart").IsAllowed);
        Assert.Contains(result.Actions!.Single(x => x.Action == "CreatePackage").Reasons,
            x => x.Code == "material.missing" && x.Classification == "ATTENTION");
    }

    [Theory]
    [InlineData("MEASURED")]
    [InlineData("MANUAL_DUMMY")]
    public void A_confirmation_or_prior_dummy_package_does_not_supply_measurements_for_a_new_measured_package(string currentMode)
    {
        var context = Ready with { ToolOffsetMode = currentMode, ReleasedToolCount = 1, ToolPreparation = null,
            ToolOffsetFacts = [new("machine", "process", "nc", "READY", null, DateTimeOffset.UnixEpoch)] };
        var result = ProductionReadinessEvaluator.Evaluate(context);
        Assert.True(result.IsReadyForProduction);
        var measured = result.Actions!.Single(x => x.Action == "CreatePackage");
        Assert.False(measured.IsAllowed);
        Assert.Contains(measured.Reasons, x => x.Code == "tool_measurements_missing" && x.Classification == "BLOCKING");
        Assert.True(ProductionActionPolicy.Evaluate(context, result, ProductionAction.CreatePackage, "MANUAL_DUMMY").IsAllowed);
    }

    [Theory]
    [InlineData(null, 1, false)]
    [InlineData(null, 0, true)]
    [InlineData(0, 1, false)]
    [InlineData(1, 1, true)]
    public void Capacity_policy_is_explicit(int? capacity, int required, bool allowed)
    {
        var context = Ready with { UsableToolPositions = capacity, RequiredToolCount = required };
        Assert.Equal(allowed, ProductionReadinessEvaluator.Evaluate(context).IsReadyForProduction);
    }

    [Fact]
    public void Dummy_offsets_preserve_engineering_and_controller_boundary()
    {
        var context = Ready with { ToolOffsetMode = "MANUAL_DUMMY", ToolPreparation = null, VerificationRequired = true };
        var result = ProductionReadinessEvaluator.Evaluate(context);
        Assert.True(result.Actions!.Single(x => x.Action == "RunStart").IsAllowed);
        Assert.False(result.Actions!.Single(x => x.Action == "RecordProduction").IsAllowed);
        Assert.True(ProductionReadinessEvaluator.Evaluate(context with { VerificationSucceeded = true })
            .Actions!.Single(x => x.Action == "RecordProduction").IsAllowed);
        Assert.False(ProductionReadinessEvaluator.Evaluate(context with { ActiveToolTableReleaseId = null }).IsReadyForProduction);
    }

    [Fact]
    public void Legacy_exception_keeps_material_gate_and_manual_machine_does_not_require_nc()
    {
        Assert.True(ProductionReadinessEvaluator.Evaluate(Ready with { ActiveProcessRevisionId = null }).IsReadyForProduction);
        Assert.False(ProductionReadinessEvaluator.Evaluate(Ready with { ActiveProcessRevisionId = null, MaterialStatus = "MISSING" }).IsReadyForProduction);
        Assert.True(ProductionReadinessEvaluator.Evaluate(Ready with { ExecutionMode = "MANUAL", Releases = [], SelectedGCodeReleaseId = null }).IsReadyForProduction);
    }

    [Fact]
    public void Evidence_stamp_changes_even_when_both_versions_remain_ready()
    {
        var before = ProductionReadinessEvaluator.Evaluate(Ready).Actions!.Single(x => x.Action == "RunStart");
        var after = ProductionReadinessEvaluator.Evaluate(Ready with { ToolPreparation = Ready.ToolPreparation! with { Version = 2 } })
            .Actions!.Single(x => x.Action == "RunStart");
        Assert.True(before.IsAllowed && after.IsAllowed);
        Assert.NotEqual(before.ContextStamp, after.ContextStamp);
    }
}
