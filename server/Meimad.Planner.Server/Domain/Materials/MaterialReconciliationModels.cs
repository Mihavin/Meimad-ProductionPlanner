namespace Meimad.Planner.Server.Domain.Materials;

internal sealed record VerifiedMaterialReceipt(
    string ReceiptId,
    string CaseId,
    int Quantity,
    DateTimeOffset ReceivedAt,
    DateTimeOffset VerifiedAt,
    string VerifiedBy,
    string? ExternalReference,
    string? Comment,
    int ReservedQuantity,
    int AvailableQuantity);

internal sealed record BatchMaterialReservation(
    string ReservationId,
    string ReceiptId,
    string ProductionBatchId,
    int Quantity,
    DateTimeOffset ReservedAt,
    string ReservedBy,
    string? Comment);

internal sealed record BatchMaterialReconciliation(
    string ProductionBatchId,
    string CaseId,
    string BatchNumber,
    int PlannedQuantity,
    int ReservedQuantity,
    int VerifiedAvailableToBatch,
    int ShortageQuantity,
    string State,
    string Message,
    IReadOnlyList<VerifiedMaterialReceipt> Receipts,
    IReadOnlyList<BatchMaterialReservation> Reservations);

internal static class MaterialReconciliationStates
{
    internal const string Ready = "READY";
    internal const string Missing = "MISSING";
    internal const string Unverified = "UNVERIFIED";
}
