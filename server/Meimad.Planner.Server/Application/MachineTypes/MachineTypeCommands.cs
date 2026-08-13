namespace Meimad.Planner.Server.Application.MachineTypes;

internal sealed record CreateMachineTypeCommand(
    string? Name,
    IReadOnlyList<string?>? Capabilities);

internal readonly record struct MachineTypeField<T>(bool IsSpecified, T Value)
{
    internal static MachineTypeField<T> Unspecified => new(false, default!);
    internal static MachineTypeField<T> Specified(T value) => new(true, value);
}

internal sealed record UpdateMachineTypeCommand(
    MachineTypeField<string?> Name,
    MachineTypeField<IReadOnlyList<string?>?> Capabilities);
