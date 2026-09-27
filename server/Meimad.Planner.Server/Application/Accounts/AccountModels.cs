namespace Meimad.Planner.Server.Application.Accounts;

/// <summary>
/// The fixed list of permissions (owner decision 2026-09-27). The Administrator edits which user
/// types hold which permissions; the built-in Administrator type holds every one. Viewing, NC and
/// tool-table viewing and production-package generation need only a signed-in account.
/// </summary>
internal static class Permissions
{
    internal const string EditCases = "cases.edit";
    internal const string ReleaseNc = "nc.release";
    internal const string PrepareTools = "toolroom.prepare";
    internal const string EditToolLibrary = "tools.catalog";
    internal const string DecideQc = "qc.decide";
    internal const string PlanMachines = "planning.board";
    internal const string ManageWorkOrders = "planning.workorders";
    internal const string VerifyMaterials = "materials.verify";
    internal const string RunOperations = "production.execute";
    internal const string ManageSetup = "setup.manage";
    internal const string ManageUsers = "users.manage";

    internal static readonly IReadOnlyList<PermissionDefinition> All =
    [
        new(EditCases, "Edit cases and operations", "Cases, Case Operations, auxiliary steps, components and model files."),
        new(ReleaseNc, "Release G-code and tool tables", "Upload NC programs and tool tables, process revisions and manufacturing programs."),
        new(PrepareTools, "Prepare tools", "Work the Tool Room queue: offsets, lengths and diameters of prepared tools."),
        new(EditToolLibrary, "Edit the tool library", "Tool catalog tools, Cimatron import and export."),
        new(DecideQc, "Decide QC", "Accept or reject Production Runs in the QC queue."),
        new(PlanMachines, "Plan machines", "Planning Board assignments and order, planning modes, priorities, Timeline pins, Production Runs, readiness inputs, E-Ink packages and downtimes."),
        new(ManageWorkOrders, "Manage Work Orders and orders", "Release Work Orders, create and change Work Orders and Orders."),
        new(VerifyMaterials, "Verify materials", "Verify Work Order material orders, material receipts and reservations."),
        new(RunOperations, "Run operations", "Start, pause, reset and finish operations and record manual reports."),
        new(ManageSetup, "Setup and integrations", "Machines, calendars, resources, postprocessors, network folder, Kitaron, CNC, E-Ink devices, reports, imports and server maintenance."),
        new(ManageUsers, "Manage users", "User accounts, user types and their permissions.")
    ];

    internal static bool IsKnown(string permission) => All.Any(definition => definition.Code == permission);

    /// <summary>The user types a new installation starts with, as the owner named them.</summary>
    internal static readonly IReadOnlyList<(string Name, string Description, string[] Permissions)> DefaultTypes =
    [
        ("QC", "Manages the QC queue.", [DecideQc]),
        ("Programmer", "Uploads G-code files and tool tables.", [ReleaseNc]),
        ("Tool Room manager", "Manages the Tool Room queue and the tool library.", [PrepareTools, EditToolLibrary]),
        ("Planning", "Manages the Planning Board and verifies material orders.", [PlanMachines, ManageWorkOrders, VerifyMaterials, RunOperations]),
        ("Technologist", "Manages Cases and operations.", [EditCases])
    ];
}

internal sealed record PermissionDefinition(string Code, string Name, string Description);

/// <summary>The account behind a request, with the permissions of all its user types.</summary>
internal sealed record SignedInUser(
    string UserId,
    string UserName,
    string DisplayName,
    bool IsAdministrator,
    IReadOnlySet<string> Permissions,
    bool MustChangePassword,
    string SessionId)
{
    internal bool Has(string permission) => IsAdministrator || Permissions.Contains(permission);
}

internal sealed record UserTypeSummary(string UserTypeId, string Name, bool IsAdministrator);

internal sealed record UserAccount(
    string UserId,
    string UserName,
    string DisplayName,
    bool IsActive,
    bool MustChangePassword,
    DateTimeOffset? LastSignInAt,
    IReadOnlyList<UserTypeSummary> Types,
    int Version,
    DateTimeOffset UpdatedAt,
    string? UpdatedBy);

internal sealed record UserType(
    string UserTypeId,
    string Name,
    string? Description,
    bool IsAdministrator,
    IReadOnlyList<string> Permissions,
    int UserCount,
    int Version,
    DateTimeOffset UpdatedAt,
    string? UpdatedBy);

internal sealed record SignInResult(string Token, DateTimeOffset ExpiresAt, SignedInUser User);

/// <summary>A request the account rules refuse; <see cref="Status"/> is the HTTP status to answer with.</summary>
internal sealed class AccountException(int status, string code, string message) : Exception(message)
{
    internal int Status { get; } = status;

    internal string Code { get; } = code;
}
