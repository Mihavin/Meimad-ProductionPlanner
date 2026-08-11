namespace Meimad.Planner.Server.Domain.Machines;

internal sealed record Machine(
    string MachineId,
    string Number,
    string Name,
    string ProcessType,
    string? AxisType,
    IReadOnlyList<string> Capabilities,
    string WorkingCalendarId,
    bool IsActive,
    bool DisplayEnabled,
    string? DisplayDeviceId,
    int BacklogCount,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? PicturePath = null);

internal sealed record MachineValues(
    string? Number,
    string? Name,
    string? ProcessType,
    string? AxisType,
    IReadOnlyList<string?>? Capabilities,
    string? WorkingCalendarId,
    bool? IsActive,
    bool? DisplayEnabled,
    string? PicturePath = null);

internal sealed record ValidatedMachineValues(
    string Number,
    string Name,
    string ProcessType,
    string? AxisType,
    IReadOnlyList<string> Capabilities,
    string WorkingCalendarId,
    bool IsActive,
    bool DisplayEnabled,
    string? PicturePath = null);
