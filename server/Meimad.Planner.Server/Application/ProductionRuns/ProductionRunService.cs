using System.Text.Json;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Domain.ProductionRuns;

namespace Meimad.Planner.Server.Application.ProductionRuns;

internal sealed class ProductionRunService
{
    private readonly IProductionRunRepository repository;

    public ProductionRunService(IProductionRunRepository repository) => this.repository = repository;

    internal Task<IReadOnlyList<ProductionRun>> ListAsync(CancellationToken token = default) => repository.ListAsync(token);
    internal async Task<ProductionRun> GetAsync(string id, CancellationToken token = default) =>
        await repository.GetAsync(Id(id, "productionRunId"), token)
        ?? throw new ProductionRunNotFoundException(id);
    internal Task<IReadOnlyList<UnallocatedBatchOperation>> ListUnallocatedAsync(CancellationToken token = default) =>
        repository.ListUnallocatedAsync(token);

    internal Task<ProductionRun> CreateAsync(
        CreateProductionRunCommand command, EditAuthority authority, CancellationToken token = default) =>
        repository.CreateAsync(Validate(command), authority, token);

    internal Task<ProductionRun> AssignAsync(
        string id, int expectedVersion, AssignProductionRunCommand command,
        EditAuthority authority, CancellationToken token = default) =>
        repository.AssignAsync(Id(id, "productionRunId"), expectedVersion,
            Validate(command), authority, token);

    internal Task<ProductionRun> UpdateCompositionAsync(
        string id, int expectedVersion, CreateProductionRunCommand command,
        EditAuthority authority, CancellationToken token = default) =>
        repository.UpdateCompositionAsync(Id(id, "productionRunId"), expectedVersion,
            Validate(command with { Assignment = null }), authority, token);

    internal Task<ProductionRun> UnassignAsync(
        string id, int expectedVersion, EditAuthority authority, CancellationToken token = default) =>
        repository.UnassignAsync(Id(id, "productionRunId"), expectedVersion, authority, token);

    internal Task<ProductionRun> CancelAsync(
        string id, int expectedVersion, string? reason,
        EditAuthority authority, CancellationToken token = default) =>
        repository.CancelAsync(Id(id, "productionRunId"), expectedVersion,
            Text(reason, "reason", 2000), authority, token);

    private static CreateProductionRunCommand Validate(CreateProductionRunCommand value)
    {
        if (value.SharedSetupSeconds < 0)
            throw Validation("sharedSetupSeconds", "non_negative_required", "Shared setup seconds cannot be negative.");
        var setupJson = string.IsNullOrWhiteSpace(value.SetupSnapshotJson) ? "{}" : value.SetupSnapshotJson.Trim();
        try
        {
            using var document = JsonDocument.Parse(setupJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
        }
        catch (JsonException)
        {
            throw Validation("setupSnapshot", "invalid_json_object", "Setup snapshot must be a JSON object.");
        }
        if (value.Programs is null || value.Programs.Count == 0)
            throw Validation("programs", "required", "A Production Run requires at least one program.");
        return value with
        {
            SetupSnapshotJson = setupJson,
            Programs = value.Programs.Select(program => program with
            {
                ManufacturingProgramId = Id(program.ManufacturingProgramId, "programs.manufacturingProgramId"),
                ProcessRevisionId = Id(program.ProcessRevisionId, "programs.processRevisionId"),
                GCodeReleaseId = OptionalId(program.GCodeReleaseId, "programs.gCodeReleaseId"),
                Outputs = program.Outputs?.Select(output => output with
                {
                    RevisionOutputId = Id(output.RevisionOutputId, "programs.outputs.revisionOutputId"),
                    BatchOperationId = Id(output.BatchOperationId, "programs.outputs.batchOperationId")
                }).ToArray() ?? []
            }).ToArray(),
            Assignment = value.Assignment is null ? null : Validate(value.Assignment)
        };
    }

    private static AssignProductionRunCommand Validate(AssignProductionRunCommand value)
    {
        var mode = value.PlanningMode?.Trim().ToLowerInvariant();
        if (mode is not ("manual" or "forward" or "backward"))
            throw Validation("planningMode", "invalid", "Planning mode must be manual, forward, or backward.");
        if (value.BacklogPosition < 0)
            throw Validation("backlogPosition", "non_negative_required", "Backlog position cannot be negative.");
        return value with
        {
            MachineId = Id(value.MachineId, "machineId"),
            PlanningMode = mode,
            OverrideReason = value.ConfirmCompatibilityOverride
                ? Text(value.OverrideReason, "overrideReason", 2000) : null
        };
    }

    private static string Id(string? value, string field)
    {
        var result = value?.Trim();
        if (string.IsNullOrEmpty(result) || result.Length > 200)
            throw Validation(field, "required", $"{field} is required.");
        return result;
    }
    private static string? OptionalId(string? value, string field) =>
        string.IsNullOrWhiteSpace(value) ? null : Id(value, field);
    private static string Text(string? value, string field, int max)
    {
        var result = value?.Trim();
        if (string.IsNullOrEmpty(result)) throw Validation(field, "required", $"{field} is required.");
        if (result.Length > max) throw Validation(field, "too_long", $"{field} is too long.");
        return result;
    }
    private static ProductionRunValidationException Validation(string field, string code, string message) => new(field, code, message);
}

internal sealed class ProductionRunValidationException(string field, string code, string message) : Exception(message)
{
    internal string Field { get; } = field;
    internal string Code { get; } = code;
}
internal sealed class ProductionRunNotFoundException(string id) : Exception($"Production Run '{id}' was not found.");
internal sealed class ProductionRunVersionConflictException(string id, int version) : Exception($"Production Run '{id}' is not at expected version {version}.");
internal sealed class ProductionRunStateException(string code, string message) : Exception(message)
{
    internal string Code { get; } = code;
}
