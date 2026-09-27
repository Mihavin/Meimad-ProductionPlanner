using Meimad.Planner.Server.Application.EditMode;

namespace Meimad.Planner.Server.Application.Kitaron;

/// <summary>
/// What a Kitaron station's route steps become in Meimad (OD-038). An undecided station imports
/// nothing and is reported by the synchronization until a planner decides it in Setup.
/// </summary>
internal static class KitaronStationRoles
{
    internal const string Undecided = "UNDECIDED";
    internal const string Machine = "MACHINE";
    internal const string Workstation = "WORKSTATION";
    internal const string External = "EXTERNAL";
    internal const string Ignore = "IGNORE";

    internal static readonly IReadOnlyList<string> All = [Undecided, Machine, Workstation, External, Ignore];
    internal static readonly IReadOnlyList<string> Suggestable = [Machine, Workstation, External, Ignore];
}

/// <summary>A Kitaron station as discovered by the synchronization plus the planner's decision.</summary>
internal sealed record KitaronStationRecord(
    int KitaronStationId,
    string StationName,
    string? StationType,
    bool Retired,
    int RouteRows,
    int PlannedRows,
    int SupplierRows,
    string SuggestedRole,
    string ImportRole,
    string? MachineType,
    string? WorkstationTypeId,
    string? ExternalResourceId,
    double DefaultMinutesPerPart,
    double DefaultMinutesPerBatch,
    int CapacityRequired,
    string? Notes,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset? DecidedAt,
    string? DecidedBy,
    int Version,
    DateTimeOffset UpdatedAt);

/// <summary>The planner's decision for one station; everything else is Kitaron-owned.</summary>
internal sealed record KitaronStationDecision(
    string ImportRole,
    string? MachineType,
    string? WorkstationTypeId,
    string? ExternalResourceId,
    double DefaultMinutesPerPart,
    double DefaultMinutesPerBatch,
    int CapacityRequired,
    string? Notes);

/// <summary>A station as read from Kitaron `TStation` with its route-row statistics.</summary>
internal sealed record KitaronDiscoveredStation(
    int KitaronStationId,
    string StationName,
    string? StationType,
    bool Retired,
    int RouteRows,
    int PlannedRows,
    int SupplierRows);

internal interface IKitaronStationRepository
{
    Task<IReadOnlyList<KitaronStationRecord>> ListAsync(CancellationToken cancellationToken);

    Task<KitaronStationRecord?> GetAsync(int kitaronStationId, CancellationToken cancellationToken);

    Task<KitaronStationRecord> DecideAsync(
        int kitaronStationId,
        KitaronStationDecision decision,
        int expectedVersion,
        EditAuthority authority,
        string? userId,
        CancellationToken cancellationToken);
}

internal sealed class KitaronStationService(IKitaronStationRepository repository)
{
    internal Task<IReadOnlyList<KitaronStationRecord>> ListAsync(CancellationToken cancellationToken = default) =>
        repository.ListAsync(cancellationToken);

    internal Task<KitaronStationRecord> DecideAsync(
        int kitaronStationId,
        string? importRole,
        string? machineType,
        string? workstationTypeId,
        string? externalResourceId,
        double defaultMinutesPerPart,
        double defaultMinutesPerBatch,
        int capacityRequired,
        string? notes,
        int expectedVersion,
        EditAuthority authority,
        string? userId,
        CancellationToken cancellationToken = default)
    {
        var role = (importRole ?? string.Empty).Trim().ToUpperInvariant();
        if (!KitaronStationRoles.All.Contains(role, StringComparer.Ordinal))
            throw new KitaronStationValidationException("importRole", "importRole must be UNDECIDED, MACHINE, WORKSTATION, EXTERNAL, or IGNORE.");
        if (!double.IsFinite(defaultMinutesPerPart) || defaultMinutesPerPart < 0 || defaultMinutesPerPart > 100_000
            || !double.IsFinite(defaultMinutesPerBatch) || defaultMinutesPerBatch < 0 || defaultMinutesPerBatch > 1_000_000)
            throw new KitaronStationValidationException("defaultMinutes", "Default minutes must be zero or greater.");
        if (capacityRequired < 1 || capacityRequired > 100)
            throw new KitaronStationValidationException("capacityRequired", "capacityRequired must be between 1 and 100.");
        var type = Optional(machineType, 200, "machineType");
        var workstationType = Optional(workstationTypeId, 200, "workstationTypeId");
        var external = Optional(externalResourceId, 200, "externalResourceId");
        if (role == KitaronStationRoles.Workstation && workstationType is null)
            throw new KitaronStationValidationException("workstationTypeId", "A WORKSTATION station needs the Workstation type its steps require.");
        if (role == KitaronStationRoles.External && external is null)
            throw new KitaronStationValidationException("externalResourceId", "An EXTERNAL station needs the External Resource its steps use.");
        var decision = new KitaronStationDecision(
            role,
            role == KitaronStationRoles.Machine ? type : null,
            role == KitaronStationRoles.Workstation ? workstationType : null,
            role == KitaronStationRoles.External ? external : null,
            defaultMinutesPerPart,
            defaultMinutesPerBatch,
            capacityRequired,
            Optional(notes, 1000, "notes"));
        return repository.DecideAsync(kitaronStationId, decision, expectedVersion, authority, userId, cancellationToken);
    }

    /// <summary>
    /// The role the Kitaron flags suggest: planned (WorkPlanning) rows make a machining station,
    /// supplier rows make subcontract work, a station without route rows is noise, and everything
    /// else is an internal Workstation step. A suggestion never imports anything by itself.
    /// </summary>
    internal static string SuggestRole(int routeRows, int plannedRows, int supplierRows)
    {
        if (routeRows == 0) return KitaronStationRoles.Ignore;
        if (supplierRows * 2 > routeRows) return KitaronStationRoles.External;
        if (plannedRows * 2 > routeRows) return KitaronStationRoles.Machine;
        return KitaronStationRoles.Workstation;
    }

    private static string? Optional(string? value, int maximum, string field)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized)) return null;
        if (normalized.Length > maximum)
            throw new KitaronStationValidationException(field, $"{field} must contain at most {maximum} characters.");
        return normalized;
    }
}

internal sealed class KitaronStationValidationException(string field, string message) : Exception(message)
{
    internal string Field { get; } = field;
}

internal sealed class KitaronStationNotFoundException(int kitaronStationId)
    : Exception($"Kitaron station {kitaronStationId} has not been discovered by a synchronization.");

internal sealed class KitaronStationVersionConflictException()
    : Exception("The Kitaron station changed since it was read. Refresh and retry.");
