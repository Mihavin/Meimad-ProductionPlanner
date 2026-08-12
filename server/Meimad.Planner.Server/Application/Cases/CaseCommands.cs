namespace Meimad.Planner.Server.Application.Cases;

internal sealed record CreateCaseCommand(
    string? PartNumber,
    string? Name,
    string? Revision,
    string? Customer,
    string? CustomerReference,
    string? PreviewPath,
    string? WorkingFolderPath,
    string? MaterialType,
    string? MaterialSpecification,
    string? RawMaterialForm,
    string? RawMaterialDimensions,
    string? Notes);

internal readonly record struct OptionalField<T>(bool IsSpecified, T Value)
{
    internal static OptionalField<T> Unspecified => new(false, default!);

    internal static OptionalField<T> Specified(T value) => new(true, value);
}

internal sealed record UpdateCaseCommand(
    OptionalField<string?> PartNumber,
    OptionalField<string?> Name,
    OptionalField<string?> Revision,
    OptionalField<string?> Customer,
    OptionalField<string?> CustomerReference,
    OptionalField<string?> PreviewPath,
    OptionalField<string?> WorkingFolderPath,
    OptionalField<string?> MaterialType,
    OptionalField<string?> MaterialSpecification,
    OptionalField<string?> RawMaterialForm,
    OptionalField<string?> RawMaterialDimensions,
    OptionalField<string?> Notes);

internal sealed record CreateCaseOperationCommand(
    int OperationNumber,
    string? Name,
    string? RequiredMachineType,
    int? SetupTimeSeconds,
    int? CycleTimePerPartSeconds,
    string? DependencyType,
    string? PredecessorCaseOperationId,
    string? SimultaneousGroupKey);

internal sealed record UpdateCaseOperationCommand(
    OptionalField<int> OperationNumber,
    OptionalField<string?> Name,
    OptionalField<string?> RequiredMachineType,
    OptionalField<int?> SetupTimeSeconds,
    OptionalField<int?> CycleTimePerPartSeconds,
    OptionalField<string?> DependencyType,
    OptionalField<string?> PredecessorCaseOperationId,
    OptionalField<string?> SimultaneousGroupKey);
