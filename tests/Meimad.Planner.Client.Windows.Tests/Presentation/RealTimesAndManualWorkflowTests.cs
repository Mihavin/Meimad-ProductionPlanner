using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Presentation;
using Meimad.Planner.Client.Windows.Views;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

/// <summary>Real Machine times on the Planning Board, the operation statistics rows and the manual production statuses.</summary>
public sealed class RealTimesAndManualWorkflowTests
{
    [Fact]
    public void A_real_median_is_labelled_and_explained_on_the_board()
    {
        var operation = new PlanningOperationViewModel(new PlanningBoardOperation(
            "operation-real", "batch-1", "B-1", "case-1", "PN-1", 10, "Mill",
            "mill", 60, 60, "not_started", "machine-1", 0,
            QaTimeAfterSetupSeconds: 600,
            LoadUnloadTimeSeconds: 40,
            PlanningCycleTimePerPartSeconds: 120,
            PlanningCycleTimeSource: "measured_median",
            TotalSetupTimeSeconds: 2400,
            UsesSetupOccupancyEstimate: true,
            SetupTimeSource: "measured_median",
            QaTimeSource: "measured_median",
            LoadUnloadTimeSource: "measured_median",
            MeasuredCycleSamples: 3,
            MeasuredSetupSamples: 1,
            MeasuredQaSamples: 1,
            MeasuredLoadUnloadSamples: 2));

        Assert.Equal("Real median 00:02:00 / part", operation.CycleTimeText);
        Assert.Equal("Real setup 00:40:00", operation.SetupEstimateText);
        Assert.Contains("last 3 cycles", operation.EstimatedTimeDetail, StringComparison.Ordinal);
        Assert.Contains("Real QC median of the last 1 inspections on this Machine: 00:10:00", operation.EstimatedTimeDetail, StringComparison.Ordinal);
        Assert.Contains("load/unload median of the last 2 parts on this Machine: 00:00:40", operation.EstimatedTimeDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void Manual_production_statuses_are_offered_only_for_machines_without_dprnt()
    {
        var manual = Operation("IN_QC", manualReporting: true);
        Assert.True(manual.CanReportWorkflow);
        Assert.True(manual.IsWorkflowInQc);
        Assert.False(manual.IsWorkflowReadyForSetup);
        Assert.Equal("Passed to QC", manual.WorkflowStatusText);
        Assert.True(manual.CanMarkFinished);

        var telemetry = Operation("IN_PRODUCTION", manualReporting: false);
        Assert.False(telemetry.CanReportWorkflow);
        Assert.True(telemetry.IsWorkflowInProduction);
        Assert.Contains("DPRNT", telemetry.WorkflowReportingToolTip, StringComparison.Ordinal);
        Assert.True(telemetry.CanMarkFinished);
    }

    [Fact]
    public void A_dprnt_machine_without_a_verified_package_offers_the_setup_statuses_but_not_production()
    {
        // Owner decision 2026-10-07: no Offset Loader reports the setup, so the planner does; the
        // Machine's DPRNT still reports production and counts parts.
        var setupByHand = Operation("READY_FOR_SETUP", manualReporting: true, manualProduction: false);
        Assert.True(setupByHand.CanReportWorkflow);
        Assert.False(setupByHand.CanReportProductionStatus);
        Assert.False(setupByHand.CanReportMachinedParts);
        Assert.Contains("no Server verification", setupByHand.WorkflowReportingToolTip, StringComparison.Ordinal);
        Assert.Contains("next cycle start starts production", setupByHand.ProductionReportingToolTip, StringComparison.Ordinal);
        Assert.Equal("This Machine counts its parts through DPRNT.", setupByHand.MachinedPartsToolTip);

        // Without the field (an older Server) a manual Machine still reports every status.
        var manual = Operation("IN_PRODUCTION", manualReporting: true);
        Assert.True(manual.CanReportProductionStatus);
        Assert.True(manual.CanReportMachinedParts);
    }

    [Fact]
    public void Statistics_rows_offer_nc_for_the_cycle_and_real_times_where_they_apply()
    {
        var machine = new PlannerOperationMachineTimes(
            "machine-1", "M1", "Mill", "gcode-1", 45, 247.5,
            new PlannerMeasuredTime(120, 3, DateTimeOffset.UnixEpoch), new PlannerMeasuredTime(2400, 1, DateTimeOffset.UnixEpoch),
            null, new PlannerMeasuredTime(40, 2, DateTimeOffset.UnixEpoch), 2100, LoadingIsPerPart: false);
        var statistics = new PlannerOperationTimeStatistics(
            "case-1", "op-1", 10, "Mill", 1, 60, 60, 0, 0, true, null, true, 2, [machine], [], []);

        var cycle = OperationTimeStatisticsWindow.TimeRow.Create(statistics, machine, "cycle");
        Assert.True(cycle.CanApplyNc);
        Assert.True(cycle.CanApplyReal);
        Assert.Equal("Real median", cycle.UsedText);

        var setup = OperationTimeStatisticsWindow.TimeRow.Create(statistics, machine, "setup");
        Assert.False(setup.CanApplyNc);
        Assert.Contains("00:35:00", setup.ApplyRealToolTip, StringComparison.Ordinal);

        var qa = OperationTimeStatisticsWindow.TimeRow.Create(statistics, machine, "qa");
        Assert.False(qa.CanApplyReal);
        Assert.Equal("Operation", qa.UsedText);

        // Automatic loading: the gap between cycles is not the load/unload time.
        var loading = OperationTimeStatisticsWindow.TimeRow.Create(statistics, machine, "load_unload");
        Assert.False(loading.CanApplyReal);
        Assert.Equal("Operation", loading.UsedText);
    }

    private static PlanningOperationViewModel Operation(string workflowStatus, bool manualReporting, bool? manualProduction = null) =>
        new(new PlanningBoardOperation(
            "operation-1", "batch-1", "B-1", "case-1", "PN-1", 10, "Mill",
            "mill", 60, 60, "in_progress", "machine-1", 0,
            WorkflowStatus: workflowStatus,
            ManualWorkflowReporting: manualReporting,
            ManualProductionReporting: manualProduction));
}
