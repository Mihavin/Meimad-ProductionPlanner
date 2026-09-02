namespace Meimad.Planner.Server.Application.Kitaron;

internal sealed record KitaronMappingSettings(
    string ModelMode,
    string Status,
    IReadOnlyList<KitaronMappingField> Fields,
    IReadOnlyList<KitaronSourceColumn> DetectedColumns,
    string? Notes,
    int Version,
    DateTimeOffset UpdatedAt);

internal sealed record KitaronMappingField(
    string TargetEntity,
    string TargetField,
    string DisplayName,
    string Description,
    bool Required,
    bool Enabled,
    string? SourceColumn,
    string Confidence,
    string Transform,
    string? Notes,
    IReadOnlyList<string> SuggestedSourceColumns,
    IReadOnlyList<string> ModelModes,
    bool ConnectorManaged = false);

internal sealed record KitaronMappingUpdate(
    string? ModelMode,
    string? Status,
    IReadOnlyList<KitaronMappingFieldUpdate>? Fields,
    string? Notes,
    int ExpectedVersion);

internal sealed record KitaronMappingFieldUpdate(
    string? TargetEntity,
    string? TargetField,
    bool Enabled,
    string? SourceColumn,
    string? Confidence,
    string? Transform,
    string? Notes);

internal sealed record KitaronMappingSelection(
    string TargetEntity,
    string TargetField,
    bool Enabled,
    string? SourceColumn,
    string Confidence,
    string Transform,
    string? Notes);

internal sealed record StoredKitaronMappingSettings(
    string ModelMode,
    string Status,
    string MappingsJson,
    string DetectedColumnsJson,
    string? Notes,
    int Version,
    DateTimeOffset UpdatedAt);

internal interface IKitaronMappingRepository
{
    Task<StoredKitaronMappingSettings> GetAsync(CancellationToken cancellationToken);

    Task<StoredKitaronMappingSettings> UpdateAsync(
        StoredKitaronMappingSettings settings,
        int expectedVersion,
        CancellationToken cancellationToken);

    Task RecordDetectedColumnsAsync(
        IReadOnlyList<KitaronSourceColumn> columns,
        DateTimeOffset detectedAt,
        CancellationToken cancellationToken);
}

internal sealed class KitaronMappingConcurrencyException : Exception
{
    internal KitaronMappingConcurrencyException()
        : base("The Kitaron mapping draft changed. Reload it and try again.")
    {
    }
}

internal sealed class KitaronMappingValidationException(
    string field,
    string message) : Exception(message)
{
    internal string Field { get; } = field;
}
