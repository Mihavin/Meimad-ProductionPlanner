using Meimad.Planner.Server.Domain.Readiness;

namespace Meimad.Planner.Server.Application.Preparation;

internal static class PreparationQueueStages
{
    internal const string ProgrammingPending = "PROGRAMMING_PENDING";
    internal const string ToolPreparationPending = "TOOL_PREPARATION_PENDING";
    internal const string SetupPending = "SETUP_PENDING";

    internal static readonly IReadOnlySet<string> All = new HashSet<string>(
        [ProgrammingPending, ToolPreparationPending, SetupPending],
        StringComparer.Ordinal);
}

internal sealed record PreparationReadinessFact(
    string Key,
    string Label,
    string State,
    string Message,
    bool IsSatisfied);

internal sealed record PreparationQueueItem(
    string Stage,
    string BatchOperationId,
    string? ProductionRunId,
    string MachineAssignmentId,
    string MachineId,
    string MachineNumber,
    string MachineName,
    string PartNumber,
    string PartName,
    string BatchNumber,
    int OperationNumber,
    string OperationName,
    string? ProcessRevisionId,
    string? GCodeReleaseId,
    string? ToolTableReleaseId,
    string WorkflowStatus,
    IReadOnlyList<PreparationReadinessFact> ReadinessFacts,
    string? CaseId = null,
    string? CaseOperationId = null);

internal sealed record PreparationQueueSource(
    string BatchOperationId,
    string? ProductionRunId,
    string MachineAssignmentId,
    string MachineId,
    string MachineNumber,
    string MachineName,
    string PartNumber,
    string PartName,
    string BatchNumber,
    int OperationNumber,
    string OperationName,
    string? LatestWorkflowEventType,
    ProductionReadinessContext ReadinessContext,
    bool HasCurrentValidProductionPackage = false,
    string? CaseId = null,
    string? CaseOperationId = null);

internal interface IPreparationQueueRepository
{
    Task<IReadOnlyList<PreparationQueueSource>> ReadSourcesAsync(
        CancellationToken cancellationToken);
}

internal sealed class PreparationQueueService(IPreparationQueueRepository repository)
{
    internal async Task<IReadOnlyList<PreparationQueueItem>> ListAsync(
        string stage,
        CancellationToken cancellationToken = default)
    {
        var normalized = stage?.Trim().ToUpperInvariant();
        if (normalized is null || !PreparationQueueStages.All.Contains(normalized))
            throw new PreparationQueueValidationException(
                "preparation_stage_invalid",
                "stage must be PROGRAMMING_PENDING, TOOL_PREPARATION_PENDING, or SETUP_PENDING.");

        var result = new List<PreparationQueueItem>();
        foreach (var source in await repository.ReadSourcesAsync(cancellationToken))
        {
            var item = PreparationQueueProjector.Project(source);
            if (item?.Stage == normalized) result.Add(item);
        }

        return result
            .OrderBy(value => value.MachineNumber, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.BatchNumber, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.OperationNumber)
            .ThenBy(value => value.BatchOperationId, StringComparer.Ordinal)
            .ToArray();
    }
}

internal static class PreparationQueueProjector
{
    private static readonly string[] NcKeys =
        [ReadinessComponentKeys.GCode, ReadinessComponentKeys.MachinePostprocessorCompatibility];

    private static readonly string[] ToolKeys =
        [ReadinessComponentKeys.ToolTable, ReadinessComponentKeys.ToolCapacity, ReadinessComponentKeys.ToolOffsets,
            ReadinessComponentKeys.ProductionPackage];

    internal static PreparationQueueItem? Project(PreparationQueueSource source)
    {
        var readiness = ProductionReadinessEvaluator.Evaluate(source.ReadinessContext);
        var manual = string.Equals(
            source.ReadinessContext.ExecutionMode, "MANUAL", StringComparison.Ordinal);
        var ncReady = manual || (source.ReadinessContext.ActiveProcessRevisionId is not null
            && GateSatisfied(readiness, NcKeys));
        var toolFactsReady = source.ReadinessContext.ActiveToolTableReleaseId is not null
            && GateSatisfied(readiness, ToolKeys.Where(key => key != ReadinessComponentKeys.ProductionPackage));
        var packageReady = source.HasCurrentValidProductionPackage;
        var workflowStatus = WorkflowStatus(source.LatestWorkflowEventType);

        string? stage;
        string[] visibleKeys;
        if (!ncReady)
        {
            stage = PreparationQueueStages.ProgrammingPending;
            visibleKeys = NcKeys;
        }
        else if (workflowStatus is not ("READY_FOR_SETUP" or "IN_SETUP" or "IN_SETUP_RUN"))
        {
            return null;
        }
        else if (!toolFactsReady || !packageReady)
        {
            stage = PreparationQueueStages.ToolPreparationPending;
            visibleKeys = ToolKeys;
        }
        else if (workflowStatus is "READY_FOR_SETUP" or "IN_SETUP" or "IN_SETUP_RUN")
        {
            stage = PreparationQueueStages.SetupPending;
            visibleKeys = [.. NcKeys, .. ToolKeys];
        }
        else
        {
            return null;
        }

        var facts = readiness.Components
            .Where(component => visibleKeys.Contains(component.Key, StringComparer.Ordinal))
            .Select(component => new PreparationReadinessFact(
                component.Key,
                component.Label,
                component.State,
                component.Message,
                Satisfied(component)))
            .ToList();
        if (visibleKeys.Contains(ReadinessComponentKeys.ProductionPackage, StringComparer.Ordinal))
        {
            facts.Add(new(
                ReadinessComponentKeys.ProductionPackage,
                "Current Production Package",
                packageReady ? ReadinessStates.Ready : ReadinessStates.Missing,
                packageReady
                    ? "A current immutable Production Package matches this Operation, assigned Machine, NC, Tool Table, and verification configuration."
                    : toolFactsReady
                        ? "Tool preparation is complete. Create the Machine-specific Production Package to make this Operation Ready for Setup."
                        : "A Production Package can be created after the current Machine-specific prerequisites are complete.",
                packageReady));
        }
        if (!manual && source.ReadinessContext.ActiveProcessRevisionId is null)
        {
            facts.Insert(0, new(
                "activeProcessRevision", "Active Process Revision", ReadinessStates.Missing,
                "No active Process Revision exists, so no current NC release can satisfy this assigned CNC operation.",
                false));
        }
        if (stage == PreparationQueueStages.ToolPreparationPending
            && source.ReadinessContext.ActiveToolTableReleaseId is null)
        {
            facts.Insert(0, new(
                "toolTableRelease", "Current Tool Table Release", ReadinessStates.Missing,
                "No current immutable Tool Table release exists for the active process.",
                false));
        }

        return new(
            stage,
            source.BatchOperationId,
            source.ProductionRunId,
            source.MachineAssignmentId,
            source.MachineId,
            source.MachineNumber,
            source.MachineName,
            source.PartNumber,
            source.PartName,
            source.BatchNumber,
            source.OperationNumber,
            source.OperationName,
            source.ReadinessContext.ActiveProcessRevisionId,
            readiness.EffectiveGCodeReleaseId,
            source.ReadinessContext.ActiveToolTableReleaseId,
            workflowStatus,
            facts.ToArray(),
            source.CaseId,
            source.CaseOperationId);
    }

    private static bool GateSatisfied(ProductionReadinessResult readiness, IEnumerable<string> keys) =>
        keys.Select(key => readiness.Components.Single(component => component.Key == key))
            .All(Satisfied);

    private static bool Satisfied(ReadinessComponent component) =>
        !component.IsBlocking
        && component.State is ReadinessStates.Ready or ReadinessStates.NotRequired;

    internal static string WorkflowStatus(string? eventType) => eventType switch
    {
        null => "READY_FOR_SETUP",
        "OFFSET_LOADER_COMPLETED" or "SETUP_VERIFICATION_REQUESTED"
            or "SETUP_VERIFICATION_FAILED" => "IN_SETUP",
        "SETUP_VERIFICATION_SUCCEEDED" or "QC_FAIL" => "IN_SETUP_RUN",
        "SEND_TO_QC" => "IN_QC",
        "QC_PASS" => "READY_FOR_PRODUCTION",
        "CYCLE_START" or "CYCLE_END" or "CYCLE_INTERRUPTED"
            or "PRODUCTION_SESSION_OPENED" => "IN_PRODUCTION",
        _ => "NOT_IN_PREPARATION"
    };
}

internal sealed class PreparationQueueValidationException(string code, string message)
    : Exception(message)
{
    internal string Code { get; } = code;
}
