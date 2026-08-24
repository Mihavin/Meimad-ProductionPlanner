using System.Text.Json;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Domain.GCode;

namespace Meimad.Planner.Server.Application.GCode;

internal sealed class ManufacturingProgramService
{
    private readonly IManufacturingProgramRepository repository;

    public ManufacturingProgramService(IManufacturingProgramRepository repository) =>
        this.repository = repository;

    internal Task<IReadOnlyList<ManufacturingProgram>> ListAsync(CancellationToken token = default) =>
        repository.ListAsync(token);

    internal async Task<ManufacturingProgram> GetAsync(string programId, CancellationToken token = default) =>
        await repository.GetAsync(Required(programId, "manufacturingProgramId"), token)
        ?? throw new ManufacturingProgramNotFoundException(programId);

    internal Task<ManufacturingProgram> CreateAsync(
        CreateManufacturingProgramCommand command,
        EditAuthority authority,
        CancellationToken token = default) =>
        repository.CreateAsync(Validate(command), authority, token);

    internal Task<ManufacturingProgram> CreateRevisionAsync(
        string programId,
        int expectedVersion,
        CreateManufacturingProgramRevisionCommand command,
        EditAuthority authority,
        CancellationToken token = default) =>
        repository.CreateRevisionAsync(
            Required(programId, "manufacturingProgramId"), expectedVersion,
            Validate(command), authority, token);

    private static CreateManufacturingProgramCommand Validate(CreateManufacturingProgramCommand command) =>
        command with
        {
            Name = Required(command.Name, "name", 200),
            SourceProcessRevisionId = Required(command.SourceProcessRevisionId, "sourceProcessRevisionId"),
            ChangeDescription = Required(command.ChangeDescription, "changeDescription", 2000),
            Outputs = ValidateOutputs(command.Outputs)
        };

    private static CreateManufacturingProgramRevisionCommand Validate(CreateManufacturingProgramRevisionCommand command) =>
        command with
        {
            SourceProcessRevisionId = Required(command.SourceProcessRevisionId, "sourceProcessRevisionId"),
            ChangeDescription = Required(command.ChangeDescription, "changeDescription", 2000),
            Outputs = ValidateOutputs(command.Outputs)
        };

    internal static IReadOnlyList<ManufacturingProgramOutputInput> ValidateOutputs(
        IReadOnlyList<ManufacturingProgramOutputInput>? outputs)
    {
        if (outputs is null || outputs.Count == 0)
            throw new ManufacturingProgramValidationException("outputs", "required", "At least one output is required.");

        var operationIds = new HashSet<string>(StringComparer.Ordinal);
        var displayOrders = new HashSet<int>();
        var normalized = new List<ManufacturingProgramOutputInput>(outputs.Count);
        foreach (var output in outputs)
        {
            var operationId = Required(output.CaseOperationId, "outputs.caseOperationId");
            if (output.QuantityPerCycle <= 0)
                throw new ManufacturingProgramValidationException(
                    "outputs.quantityPerCycle", "positive_required", "Output quantity per cycle must be positive.");
            if (output.DisplayOrder < 0)
                throw new ManufacturingProgramValidationException(
                    "outputs.displayOrder", "non_negative_required", "Output display order cannot be negative.");
            if (!operationIds.Add(operationId))
                throw new ManufacturingProgramValidationException(
                    "outputs.caseOperationId", "duplicate_output", "A Case Operation may appear only once in a revision recipe.");
            if (!displayOrders.Add(output.DisplayOrder))
                throw new ManufacturingProgramValidationException(
                    "outputs.displayOrder", "duplicate_display_order", "Output display order must be unique in a revision recipe.");

            var metadata = string.IsNullOrWhiteSpace(output.ExecutionMetadataJson)
                ? "{}"
                : output.ExecutionMetadataJson.Trim();
            try
            {
                using var document = JsonDocument.Parse(metadata);
                if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
            }
            catch (JsonException)
            {
                throw new ManufacturingProgramValidationException(
                    "outputs.executionMetadata", "invalid_json_object", "Execution metadata must be a JSON object.");
            }
            normalized.Add(output with { CaseOperationId = operationId, ExecutionMetadataJson = metadata });
        }
        return normalized.OrderBy(value => value.DisplayOrder).ToArray();
    }

    private static string Required(string? value, string field, int maximum = 200)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized))
            throw new ManufacturingProgramValidationException(field, "required", $"{field} is required.");
        if (normalized.Length > maximum)
            throw new ManufacturingProgramValidationException(field, "too_long", $"{field} may contain at most {maximum} characters.");
        return normalized;
    }
}

internal sealed class ManufacturingProgramValidationException(
    string field, string code, string message) : Exception(message)
{
    internal string Field { get; } = field;
    internal string Code { get; } = code;
}

internal sealed class ManufacturingProgramNotFoundException(string id) :
    Exception($"Manufacturing Program '{id}' was not found.");
internal sealed class ManufacturingProgramSourceRevisionNotFoundException(string id) :
    Exception($"Source process revision '{id}' was not found.");
internal sealed class ManufacturingProgramOutputOperationNotFoundException(string id) :
    Exception($"Output Case Operation '{id}' was not found.");
internal sealed class ManufacturingProgramVersionConflictException(string id, int expected) :
    Exception($"Manufacturing Program '{id}' is not at expected version {expected}.");
