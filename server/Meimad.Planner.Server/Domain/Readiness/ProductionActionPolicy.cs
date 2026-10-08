using System.Security.Cryptography;
using System.Text.Json;
using System.Globalization;
using Meimad.Planner.Server.Domain.GCode;

namespace Meimad.Planner.Server.Domain.Readiness;

internal enum ProductionAction { Plan, CreatePackage, RecordSetupStart, RunStart, RecordProduction }

internal sealed record ProductionActionReason(
    string Code, string RequiredEvidence, string CurrentEvidence, string Classification, string Message);

internal sealed record ProductionActionDecision(
    string Action, bool IsAllowed, string ContextStamp, IReadOnlyList<ProductionActionReason> Reasons);

/// <summary>Preparation gates only. Allocation, permissions and exact controller verification
/// remain independent transaction-owned invariants; permission to plan never asserts physical readiness.</summary>
internal static class ProductionActionPolicy
{
    internal const string UnknownCapacityPolicy = "REQUIRE_KNOWN_CAPACITY_WHEN_TOOLS_REQUIRED";
    internal static ProductionActionDecision Evaluate(ProductionReadinessContext context,
        ProductionReadinessResult readiness, ProductionAction action, string offsetMode = "MEASURED")
    {
        var reasons = readiness.Components.Select(component =>
        {
            var required = action switch
            {
                ProductionAction.Plan => false,
                ProductionAction.CreatePackage or ProductionAction.RecordSetupStart => component.Key != ReadinessComponentKeys.Material,
                _ => true
            };
            if (offsetMode == "MANUAL_DUMMY" && component.Key == ReadinessComponentKeys.ToolOffsets)
                required = false;
            if (action == ProductionAction.CreatePackage && context.ReplacedGCodeReleaseId is not null
                && readiness.EffectiveGCodeReleaseId is not null && component.Key == ReadinessComponentKeys.GCode)
                required = false;
            var missing = component.IsBlocking;
            var evidence = component.Key switch
            {
                ReadinessComponentKeys.GCode => readiness.EffectiveGCodeReleaseId ?? context.SelectedGCodeReleaseId,
                ReadinessComponentKeys.ToolTable => context.ActiveToolTableReleaseId,
                ReadinessComponentKeys.ToolCapacity => $"required={context.RequiredToolCount?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}; available={context.UsableToolPositions?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}; policy={UnknownCapacityPolicy}",
                ReadinessComponentKeys.ToolOffsets => $"mode={context.ToolOffsetMode}; preparationVersion={context.ToolPreparation?.Version.ToString(CultureInfo.InvariantCulture) ?? "none"}",
                ReadinessComponentKeys.MachinePostprocessorCompatibility => context.MachineId,
                _ => null
            };
            var code = component.Key == ReadinessComponentKeys.ToolCapacity && component.IsBlocking
                ? ToolCapacityEvaluator.Evaluate(context.RequiredToolCount, context.UsableToolPositions).Code
                : component.State.ToLowerInvariant();
            return new ProductionActionReason(component.Key + "." + code,
                component.Label, evidence is null ? component.State : component.State + ": " + evidence,
                missing ? required ? "BLOCKING" : "ATTENTION" : "SATISFIED", component.Message);
        }).ToList();
        if (action != ProductionAction.Plan && context.AmbiguousExecutionContext
            && !reasons.Any(x => x.Code == "executionContext.blocked"))
            reasons.Add(new("executionContext.blocked", "Exact Run/program/output context", "AMBIGUOUS", "BLOCKING",
                "Select an exact Run/program/output context for this Operation."));
        if (action != ProductionAction.Plan && (context.MachineAssignmentId is null || context.MachineId is null))
            reasons.Add(new("assignment_required", "Exact Machine assignment", "MISSING", "BLOCKING", "This action requires a current Machine assignment."));
        if (action == ProductionAction.CreatePackage)
        {
            if (context.ActiveToolTableReleaseId is null)
                reasons.Add(new("tool_table_missing", "Current released tool table", "MISSING", "BLOCKING", "Package creation requires a current released tool table."));
            if (context.ExecutionMode == "CNC_GCODE" && readiness.EffectiveGCodeReleaseId is null)
                reasons.Add(new("gcode_missing", "Current compatible NC release", "MISSING", "BLOCKING", "A CNC package requires a current compatible NC release."));
            if (offsetMode == "MEASURED" && context.ReleasedToolCount > 0 &&
                (context.ToolPreparation is not { } preparation || preparation.ToolTableReleaseId != context.ActiveToolTableReleaseId
                    || preparation.UnmeasuredRequiredCount > 0))
                reasons.Add(new("tool_measurements_missing", "Tool Room measurements for the released tools", "MISSING", "BLOCKING",
                    "Tool Room measurements are missing or incomplete for this Machine and Tool Table release."));
        }
        if (action == ProductionAction.RecordSetupStart)
        {
            var observed = context.LoaderExecutionObserved || context.ManualSetupReportingSupported;
            reasons.Add(new("setup_execution_evidence", "Actual loader execution or supported manual setup report",
                context.LoaderExecutionObserved ? "LOADER_OBSERVED" : context.ManualSetupReportingSupported ? "MANUAL_REPORT_SUPPORTED" : "MISSING",
                observed ? "SATISFIED" : "BLOCKING", "Exporting a package does not record setup start."));
        }
        if (action == ProductionAction.RecordProduction && context.VerificationRequired)
            reasons.Add(new("controller_verification", "Successful exact Run/Machine/NC/current Offset Loader binding",
                context.VerificationSucceeded ? "SUCCEEDED" : "UNVERIFIED",
                context.VerificationSucceeded ? "SATISFIED" : "BLOCKING",
                "Run Start does not establish controller verification or physical production authority."));
        // Stable ordering avoids HashSet/database row-order making an unchanged context look stale.
        var stamp = Stamp(new { context = context with {
            SupportedPostprocessorIds = new SortedSet<string>(context.SupportedPostprocessorIds, StringComparer.Ordinal),
            Releases = context.Releases.OrderBy(x => x.GCodeReleaseId, StringComparer.Ordinal).ToArray(),
            ToolOffsetFacts = context.ToolOffsetFacts.OrderBy(x => x.MachineId, StringComparer.Ordinal)
                .ThenBy(x => x.ProcessRevisionId, StringComparer.Ordinal).ThenBy(x => x.GCodeReleaseId, StringComparer.Ordinal)
                .ThenBy(x => x.RecordedAt).ThenBy(x => x.Status, StringComparer.Ordinal).ThenBy(x => x.Comment, StringComparer.Ordinal).ToArray()
        }, action, offsetMode });
        return new(action.ToString(), reasons.All(x => x.Classification != "BLOCKING"), stamp, reasons);
    }

    internal static string Stamp<T>(T evidence) => Convert.ToHexString(
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(evidence))).ToLowerInvariant();
}
