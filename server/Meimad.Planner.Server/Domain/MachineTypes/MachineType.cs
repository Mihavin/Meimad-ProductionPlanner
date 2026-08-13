namespace Meimad.Planner.Server.Domain.MachineTypes;

internal sealed record MachineType(
    string MachineTypeId,
    string Name,
    IReadOnlyList<string> Capabilities,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

internal sealed record MachineTypeValues(
    string? Name,
    IReadOnlyList<string?>? Capabilities);

internal sealed record ValidatedMachineTypeValues(
    string Name,
    IReadOnlyList<string> Capabilities);
