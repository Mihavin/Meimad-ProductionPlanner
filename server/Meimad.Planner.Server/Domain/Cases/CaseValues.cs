namespace Meimad.Planner.Server.Domain.Cases;

internal sealed record CaseValues(
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
    int? CurrentSetupTimeSeconds,
    int? CurrentCycleTimePerPartSeconds,
    string? Notes);

internal sealed record ValidatedCaseValues(
    string PartNumber,
    string Name,
    string? Revision,
    string? Customer,
    string? CustomerReference,
    string? PreviewPath,
    string WorkingFolderPath,
    string? MaterialType,
    string? MaterialSpecification,
    string? RawMaterialForm,
    string? RawMaterialDimensions,
    int? CurrentSetupTimeSeconds,
    int? CurrentCycleTimePerPartSeconds,
    string? Notes);
