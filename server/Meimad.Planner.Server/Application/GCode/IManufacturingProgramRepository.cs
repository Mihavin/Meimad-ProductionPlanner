using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Domain.GCode;

namespace Meimad.Planner.Server.Application.GCode;

internal interface IManufacturingProgramRepository
{
    Task<IReadOnlyList<ManufacturingProgram>> ListAsync(CancellationToken token);
    Task<ManufacturingProgram?> GetAsync(string programId, CancellationToken token);
    Task<ManufacturingProgram> CreateAsync(
        CreateManufacturingProgramCommand command,
        EditAuthority authority,
        CancellationToken token);
    Task<ManufacturingProgram> CreateRevisionAsync(
        string programId,
        int expectedVersion,
        CreateManufacturingProgramRevisionCommand command,
        EditAuthority authority,
        CancellationToken token);
}

internal sealed record ManufacturingProgramOutputInput(
    string? CaseOperationId,
    int QuantityPerCycle,
    int DisplayOrder,
    string? ExecutionMetadataJson);

internal sealed record CreateManufacturingProgramCommand(
    string? Name,
    string? SourceProcessRevisionId,
    string? ChangeDescription,
    IReadOnlyList<ManufacturingProgramOutputInput>? Outputs);

internal sealed record CreateManufacturingProgramRevisionCommand(
    string? SourceProcessRevisionId,
    string? ChangeDescription,
    IReadOnlyList<ManufacturingProgramOutputInput>? Outputs);
