namespace Meimad.Planner.Server.Application.Machines;

internal sealed record CreateMachineCommand(
    string? Number,
    string? Name,
    string? ProcessType,
    string? AxisType,
    IReadOnlyList<string?>? Capabilities,
    string? WorkingCalendarId,
    bool? IsActive,
    bool? DisplayEnabled,
    string? PicturePath = null,
    string? MachineTypeId = null,
    bool? RespectMasterCalendar = true);

internal readonly record struct MachineField<T>(bool IsSpecified, T Value)
{
    internal static MachineField<T> Unspecified => new(false, default!);

    internal static MachineField<T> Specified(T value) => new(true, value);
}

internal sealed record UpdateMachineCommand(
    MachineField<string?> Number,
    MachineField<string?> Name,
    MachineField<string?> ProcessType,
    MachineField<string?> AxisType,
    MachineField<IReadOnlyList<string?>?> Capabilities,
    MachineField<string?> WorkingCalendarId,
    MachineField<bool?> IsActive,
    MachineField<bool?> DisplayEnabled,
    MachineField<string?> PicturePath = default,
    MachineField<string?> MachineTypeId = default,
    MachineField<bool?> RespectMasterCalendar = default);
