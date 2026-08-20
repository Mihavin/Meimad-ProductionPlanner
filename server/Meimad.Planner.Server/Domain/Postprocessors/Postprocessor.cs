namespace Meimad.Planner.Server.Domain.Postprocessors;

internal sealed record Postprocessor(
    string PostprocessorId,
    string Name,
    string? Description,
    bool IsActive,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

internal sealed record PostprocessorValues(
    string? Name,
    string? Description,
    bool? IsActive);

internal sealed record ValidatedPostprocessorValues(
    string Name,
    string? Description,
    bool IsActive);
