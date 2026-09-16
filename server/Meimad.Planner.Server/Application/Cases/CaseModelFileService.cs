using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Domain.Cases;

namespace Meimad.Planner.Server.Application.Cases;

internal sealed record CaseModelFileCreate(
    string FilePath,
    string? Kind,
    string? Label,
    string? CaseOperationId,
    bool IsPrimary);

internal sealed record CaseModelFileUpdate(
    string? Kind,
    string? Label,
    string? CaseOperationId,
    bool ClearCaseOperation,
    bool? IsPrimary,
    int? SortOrder);

internal sealed class CaseModelFileValidationException(string field, string code, string message)
    : Exception(message)
{
    public string Field { get; } = field;
    public string Code { get; } = code;
}

internal sealed class CaseModelFileNotFoundException(string caseModelFileId)
    : Exception($"Case model file '{caseModelFileId}' was not found.")
{
    public string CaseModelFileId { get; } = caseModelFileId;
}

internal sealed class CaseModelFileVersionConflictException(string caseModelFileId, int expectedVersion)
    : Exception($"Case model file '{caseModelFileId}' is not at version {expectedVersion}.")
{
    public string CaseModelFileId { get; } = caseModelFileId;
    public int ExpectedVersion { get; } = expectedVersion;
}

internal interface ICaseModelFileRepository
{
    Task<IReadOnlyList<CaseModelFile>> ListAsync(string caseId, CancellationToken cancellationToken);

    Task<CaseModelFile?> GetAsync(string caseId, string caseModelFileId, CancellationToken cancellationToken);

    /// <summary>
    /// Inserts the file. Throws <see cref="CaseModelFileValidationException"/> when the Case does
    /// not exist or the Operation does not belong to it.
    /// </summary>
    Task<CaseModelFile> CreateAsync(CaseModelFile file, EditAuthority editAuthority, CancellationToken cancellationToken);

    Task<CaseModelFile> UpdateAsync(
        string caseId,
        string caseModelFileId,
        int expectedVersion,
        CaseModelFileUpdate update,
        DateTimeOffset now,
        EditAuthority editAuthority,
        CancellationToken cancellationToken);

    Task<bool> DeleteAsync(string caseId, string caseModelFileId, EditAuthority editAuthority, CancellationToken cancellationToken);
}

internal sealed class CaseModelFileService(ICaseModelFileRepository repository, TimeProvider timeProvider)
{
    public Task<IReadOnlyList<CaseModelFile>> ListAsync(string caseId, CancellationToken cancellationToken) =>
        repository.ListAsync(Required(caseId, "caseId"), cancellationToken);

    public Task<CaseModelFile?> GetAsync(string caseId, string caseModelFileId, CancellationToken cancellationToken) =>
        repository.GetAsync(Required(caseId, "caseId"), Required(caseModelFileId, "caseModelFileId"), cancellationToken);

    public Task<CaseModelFile> CreateAsync(
        string caseId,
        CaseModelFileCreate create,
        EditAuthority editAuthority,
        CancellationToken cancellationToken)
    {
        var filePath = create.FilePath?.Trim() ?? string.Empty;
        if (filePath.Length == 0)
        {
            throw new CaseModelFileValidationException("filePath", "required", "filePath is required.");
        }

        var format = CaseModelFileFormats.FromPath(filePath)
            ?? throw new CaseModelFileValidationException(
                "filePath", "unsupported_format", "Choose a .stp, .step, or .stl file.");
        var kind = NormalizeKind(create.Kind ?? CaseModelFileKinds.Part);
        var label = string.IsNullOrWhiteSpace(create.Label)
            ? System.IO.Path.GetFileName(filePath)
            : create.Label.Trim();
        var now = timeProvider.GetUtcNow();
        var file = new CaseModelFile(
            Guid.NewGuid().ToString("N"),
            Required(caseId, "caseId"),
            NullIfBlank(create.CaseOperationId),
            kind,
            format,
            filePath,
            label,
            create.IsPrimary,
            SortOrder: 0,
            Version: 1,
            now,
            now);
        return repository.CreateAsync(file, editAuthority, cancellationToken);
    }

    public Task<CaseModelFile> UpdateAsync(
        string caseId,
        string caseModelFileId,
        int expectedVersion,
        CaseModelFileUpdate update,
        EditAuthority editAuthority,
        CancellationToken cancellationToken)
    {
        var kind = update.Kind is null ? null : NormalizeKind(update.Kind);
        string? label = null;
        if (update.Label is not null)
        {
            label = update.Label.Trim();
            if (label.Length == 0)
            {
                throw new CaseModelFileValidationException("label", "required", "label must not be blank.");
            }
        }

        if (update.SortOrder is < 0)
        {
            throw new CaseModelFileValidationException("sortOrder", "invalid_value", "sortOrder must be zero or positive.");
        }

        return repository.UpdateAsync(
            Required(caseId, "caseId"),
            Required(caseModelFileId, "caseModelFileId"),
            expectedVersion,
            update with { Kind = kind, Label = label, CaseOperationId = NullIfBlank(update.CaseOperationId) },
            timeProvider.GetUtcNow(),
            editAuthority,
            cancellationToken);
    }

    public Task<bool> DeleteAsync(
        string caseId,
        string caseModelFileId,
        EditAuthority editAuthority,
        CancellationToken cancellationToken) =>
        repository.DeleteAsync(Required(caseId, "caseId"), Required(caseModelFileId, "caseModelFileId"), editAuthority, cancellationToken);

    private static string NormalizeKind(string value)
    {
        var kind = value.Trim().ToLowerInvariant();
        if (!CaseModelFileKinds.IsValid(kind))
        {
            throw new CaseModelFileValidationException(
                "kind", "invalid_value", $"kind must be one of: {string.Join(", ", CaseModelFileKinds.All)}.");
        }

        return kind;
    }

    private static string Required(string? value, string field) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new CaseModelFileValidationException(field, "required", $"{field} is required.")
            : value.Trim();

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
