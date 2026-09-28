namespace Meimad.Planner.Server.Application.Kitaron.Push;

/// <summary>A Kitaron <c>TSubRootCard</c> column the Planner may write.</summary>
internal sealed record KitaronPushTarget(string Column, string Kind, string Name, string Description);

/// <summary>A value the Planner knows about a Work Order operation.</summary>
internal sealed record KitaronPushSource(string Code, string Kind, string Name, string Description);

/// <summary>
/// What may be pushed, and from where (owner decision 2026-09-28). Every other date or quantity
/// column of <c>TSubRootCard</c> is rewritten by Kitaron's own automatic work planning or production
/// reporting (read-only check of its procedures and triggers, 2026-09-28), so it is not offered;
/// check a column the same way before adding it here.
/// </summary>
internal static class KitaronPushCatalog
{
    internal const string DateKind = "date";
    internal const string NumberKind = "number";

    internal static readonly IReadOnlyList<KitaronPushTarget> Targets =
    [
        new("OperationQty", NumberKind, "Operation quantity",
            "Empty in Kitaron; Kitaron copies it from the route master only when route cards are updated."),
        new("StartDateReal", DateKind, "Real start date",
            "Kitaron also sets it from production reports entered in Kitaron, and its automatic work planning treats the operation as started."),
        new("FinishDateCalc", DateKind, "Calculated finish date",
            "Empty in Kitaron and not used by Kitaron's own logic."),
        new("SetupTimeReal", NumberKind, "Real setup time (minutes)",
            "Kitaron also recalculates it from setup reports entered in Kitaron; its work planning subtracts it from the remaining setup time.")
    ];

    internal static readonly IReadOnlyList<KitaronPushSource> Sources =
    [
        new("actual_start", DateKind, "Actual start", "When the operation was started in the Planner or by the first CNC cycle."),
        new("actual_finish", DateKind, "Actual finish", "When the operation was finished in the Planner."),
        new("forecast_start", DateKind, "Forecast start (Timeline)", "The operation's start as the Timeline calculates it now."),
        new("forecast_finish", DateKind, "Forecast finish (Timeline)", "The operation's finish as the Timeline calculates it now."),
        new("setup_minutes", NumberKind, "Setup minutes (start to QC PASS)", "Minutes from the operation's start until QC approved its first part."),
        new("good_quantity", NumberKind, "Good quantity made", "Parts produced so far on the operation, counted from CNC cycles."),
        new("planned_quantity", NumberKind, "Planned quantity", "The Work Order's planned quantity.")
    ];

    internal static KitaronPushTarget? Target(string column) =>
        Targets.FirstOrDefault(target => string.Equals(target.Column, column, StringComparison.OrdinalIgnoreCase));

    internal static KitaronPushSource? Source(string code) =>
        Sources.FirstOrDefault(source => string.Equals(source.Code, code, StringComparison.OrdinalIgnoreCase));
}

internal sealed record KitaronPushMapping(string KitaronColumn, string PlannerValue, bool Enabled);

internal sealed record KitaronPushSettings(
    bool Enabled,
    int IntervalMinutes,
    IReadOnlyList<KitaronPushMapping> Mappings,
    int Version,
    DateTimeOffset UpdatedAt,
    string? UpdatedBy);

/// <summary>A Planner Work Order operation that came from Kitaron, with the values it can push.</summary>
internal sealed record KitaronPushOperation(
    string BatchOperationId,
    int WorkOrderNumber,
    int OperationNumber,
    string PartNumber,
    string OperationName,
    DateTimeOffset? ActualStart,
    DateTimeOffset? ActualEnd,
    DateTimeOffset? FirstQcPassAt,
    int GoodQuantity,
    int PlannedQuantity);

internal sealed record KitaronPushForecast(DateTimeOffset StartsAt, DateTimeOffset EndsAt);

/// <summary>One <c>TSubRootCard</c> row with the current values of the pushed columns.</summary>
internal sealed record KitaronOperationRow(
    long RowId,
    int WorkOrderNumber,
    string ActionNumber,
    bool WorkOrderClosed,
    IReadOnlyDictionary<string, object?> Values);

internal sealed record KitaronPushWrite(long RowId, int WorkOrderNumber, string Column, object Value);

internal sealed record KitaronPushChange(
    int WorkOrderNumber,
    string ActionNumber,
    long KitaronRowId,
    string PartNumber,
    string OperationName,
    string KitaronColumn,
    string? OldValue,
    string NewValue);

internal sealed record KitaronPushResult(
    string? RunId,
    bool Applied,
    DateTimeOffset At,
    int OperationsMatched,
    int OperationsSkipped,
    IReadOnlyList<KitaronPushChange> Changes,
    IReadOnlyList<string> Notes);

internal sealed record KitaronPushRunSummary(
    string RunId,
    string Trigger,
    string? RequestedBy,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string Status,
    int OperationsMatched,
    int ValuesWritten,
    int OperationsSkipped,
    string? Message);

internal sealed class KitaronPushBlockedException(string message) : Exception(message);

internal sealed class KitaronPushValidationException(string field, string message) : Exception(message)
{
    internal string Field { get; } = field;
}

internal interface IKitaronPushRepository
{
    Task<KitaronPushSettings> GetSettingsAsync(CancellationToken cancellationToken);

    Task<KitaronPushSettings> UpdateSettingsAsync(
        bool enabled, int intervalMinutes, IReadOnlyList<KitaronPushMapping> mappings, int expectedVersion,
        string actor, DateTimeOffset now, CancellationToken cancellationToken);

    Task<IReadOnlyList<KitaronPushOperation>> ReadOperationsAsync(CancellationToken cancellationToken);

    Task StartRunAsync(string runId, string trigger, string? requestedBy, DateTimeOffset now, CancellationToken cancellationToken);

    Task FinishRunAsync(
        string runId, bool succeeded, int operationsMatched, int operationsSkipped, string? message,
        IReadOnlyList<KitaronPushChange> writtenChanges, DateTimeOffset now, CancellationToken cancellationToken);

    Task<DateTimeOffset?> LastAutomaticRunStartedAtAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<KitaronPushRunSummary>> ListRunsAsync(int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<KitaronPushChange>> ListChangesAsync(string runId, CancellationToken cancellationToken);
}

/// <summary>Kitaron's SQL Server: reads the current values and writes the changes in one transaction.</summary>
internal interface IKitaronPushTarget
{
    Task<IReadOnlyList<KitaronOperationRow>> ReadAsync(
        StoredKitaronConnectionSettings connection, string password, IReadOnlyCollection<int> workOrderNumbers,
        IReadOnlyList<string> columns, CancellationToken cancellationToken);

    Task WriteAsync(
        StoredKitaronConnectionSettings connection, string password, IReadOnlyList<KitaronPushWrite> writes,
        CancellationToken cancellationToken);
}

/// <summary>The Timeline's current start and finish of each operation it places.</summary>
internal interface IKitaronPushForecast
{
    Task<IReadOnlyDictionary<string, KitaronPushForecast>> ReadAsync(CancellationToken cancellationToken);
}
