using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Domain.Materials;

namespace Meimad.Planner.Server.Application.Materials;

internal interface IMaterialReconciliationRepository
{
    Task<IReadOnlyList<VerifiedMaterialReceipt>> ListReceiptsAsync(
        string caseId, CancellationToken cancellationToken);

    Task<VerifiedMaterialReceipt> CreateReceiptAsync(
        CreateVerifiedMaterialReceiptCommand command,
        DateTimeOffset verifiedAt,
        EditAuthority authority,
        CancellationToken cancellationToken);

    Task<BatchMaterialReconciliation?> ReadBatchAsync(
        string batchId, CancellationToken cancellationToken);

    Task<BatchMaterialReconciliation?> ReplaceReservationsAsync(
        string batchId,
        IReadOnlyList<MaterialReservationValue> reservations,
        DateTimeOffset now,
        EditAuthority authority,
        CancellationToken cancellationToken);
}

internal sealed record CreateVerifiedMaterialReceiptCommand(
    string? CaseId,
    int Quantity,
    DateTimeOffset ReceivedAt,
    string? ExternalReference,
    string? Comment);

internal sealed record MaterialReservationValue(
    string? ReceiptId,
    int Quantity,
    string? Comment);

internal sealed class MaterialReconciliationService(
    IMaterialReconciliationRepository repository,
    TimeProvider timeProvider)
{
    internal Task<IReadOnlyList<VerifiedMaterialReceipt>> ListReceiptsAsync(
        string caseId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(caseId))
            throw Validation("caseId", "required", "caseId is required.");
        return repository.ListReceiptsAsync(caseId.Trim(), cancellationToken);
    }

    internal Task<VerifiedMaterialReceipt> CreateReceiptAsync(
        CreateVerifiedMaterialReceiptCommand command,
        EditAuthority authority,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        if (string.IsNullOrWhiteSpace(command.CaseId))
            throw Validation("caseId", "required", "caseId is required.");
        if (command.Quantity <= 0)
            throw Validation("quantity", "positive_required", "quantity must be greater than zero.");
        if (command.ExternalReference?.Trim().Length > 200)
            throw Validation("externalReference", "too_long", "externalReference must contain at most 200 characters.");
        if (command.Comment?.Trim().Length > 2000)
            throw Validation("comment", "too_long", "comment must contain at most 2000 characters.");
        if (command.ReceivedAt == default)
            throw Validation("receivedAt", "required", "receivedAt is required.");
        if (command.ReceivedAt > now.AddMinutes(5))
            throw Validation("receivedAt", "future_not_allowed", "receivedAt cannot be more than five minutes in the future.");

        return repository.CreateReceiptAsync(
            command with
            {
                CaseId = command.CaseId.Trim(),
                ExternalReference = Clean(command.ExternalReference),
                Comment = Clean(command.Comment)
            },
            now, authority, cancellationToken);
    }

    internal Task<BatchMaterialReconciliation?> ReadBatchAsync(
        string batchId, CancellationToken cancellationToken = default) =>
        repository.ReadBatchAsync(batchId, cancellationToken);

    internal Task<BatchMaterialReconciliation?> ReplaceReservationsAsync(
        string batchId,
        IReadOnlyList<MaterialReservationValue>? reservations,
        EditAuthority authority,
        CancellationToken cancellationToken = default)
    {
        reservations ??= [];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < reservations.Count; index++)
        {
            var item = reservations[index];
            if (string.IsNullOrWhiteSpace(item.ReceiptId))
                throw Validation($"reservations[{index}].receiptId", "required", "receiptId is required.");
            if (!seen.Add(item.ReceiptId.Trim()))
                throw Validation($"reservations[{index}].receiptId", "duplicate", "Each receipt may be reserved only once per Batch.");
            if (item.Quantity <= 0)
                throw Validation($"reservations[{index}].quantity", "positive_required", "Reservation quantity must be greater than zero; omit empty rows.");
            if (item.Comment?.Trim().Length > 2000)
                throw Validation($"reservations[{index}].comment", "too_long", "comment must contain at most 2000 characters.");
        }

        var normalized = reservations.Select(item => new MaterialReservationValue(
            item.ReceiptId!.Trim(), item.Quantity, Clean(item.Comment))).ToArray();
        return repository.ReplaceReservationsAsync(
            batchId, normalized, timeProvider.GetUtcNow(), authority, cancellationToken);
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static MaterialReconciliationValidationException Validation(
        string field, string code, string message) => new(field, code, message);
}

internal sealed class MaterialReconciliationValidationException(
    string field, string code, string message) : Exception(message)
{
    internal string Field { get; } = field;
    internal string Code { get; } = code;
}

internal sealed class MaterialReceiptCaseNotFoundException(string caseId)
    : Exception($"Case '{caseId}' was not found.");
