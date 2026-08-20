using Meimad.Planner.Server.Domain.Readiness;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal static class SqliteReadinessAudit
{
    internal static async Task AppendEvaluationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProductionReadinessContext context,
        ProductionReadinessResult? before,
        ProductionReadinessResult after,
        DateTimeOffset timestamp,
        string actor,
        string reason,
        CancellationToken token)
    {
        var entities = RelatedEntities(context);
        if (before is null || before.OverallState != after.OverallState)
        {
            await SqliteStructuredEventLogRepository.AppendAsync(
                connection,
                transaction,
                new(
                    "production_readiness_transition",
                    timestamp,
                    actor,
                    entities,
                    reason,
                    after.Summary,
                    before is null ? null : new
                    {
                        before.OverallState,
                        before.IsReadyForProduction,
                        before.Summary
                    },
                    new
                    {
                        after.OverallState,
                        after.IsReadyForProduction,
                        after.Summary,
                        after.EffectiveGCodeReleaseId
                    }),
                token);
        }

        var compatibility = after.Components.FirstOrDefault(component =>
            component.Key == ReadinessComponentKeys.MachinePostprocessorCompatibility);
        if (compatibility is { IsBlocking: true })
        {
            await AppendBlockingReasonAsync(
                connection, transaction, context, entities,
                "machine_compatibility_failure", compatibility,
                timestamp, actor, reason,
                $"readiness:compatibility:{context.BatchOperationId}:{context.ActiveProcessRevisionId}:{context.MachineId}",
                token);
        }

        var capacity = after.Components.FirstOrDefault(component =>
            component.Key == ReadinessComponentKeys.ToolCapacity);
        if (capacity is { IsBlocking: true })
        {
            await AppendBlockingReasonAsync(
                connection, transaction, context, entities,
                "tool_capacity_mismatch", capacity,
                timestamp, actor, reason,
                $"readiness:capacity:{context.BatchOperationId}:{context.ActiveProcessRevisionId}:{context.MachineId}:{context.RequiredToolCount}:{context.UsableToolPositions}",
                token);
        }
    }

    private static Task AppendBlockingReasonAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProductionReadinessContext context,
        IReadOnlyDictionary<string, string> entities,
        string eventType,
        ReadinessComponent component,
        DateTimeOffset timestamp,
        string actor,
        string reason,
        string eventKey,
        CancellationToken token) => SqliteStructuredEventLogRepository.AppendAsync(
        connection,
        transaction,
        new(
            eventType,
            timestamp,
            actor,
            entities,
            reason,
            component.Message,
            null,
            new
            {
                component.State,
                component.Message,
                context.RequiredToolCount,
                context.UsableToolPositions,
                context.SupportedPostprocessorIds
            },
            eventKey),
        token);

    private static Dictionary<string, string> RelatedEntities(
        ProductionReadinessContext context)
    {
        var values = new Dictionary<string, string>
        {
            ["batchOperationId"] = context.BatchOperationId
        };
        Add(values, "machineAssignmentId", context.MachineAssignmentId);
        Add(values, "machineId", context.MachineId);
        Add(values, "processRevisionId", context.ActiveProcessRevisionId);
        return values;
    }

    private static void Add(Dictionary<string, string> values, string key, string? value)
    {
        if (value is not null) values[key] = value;
    }
}
