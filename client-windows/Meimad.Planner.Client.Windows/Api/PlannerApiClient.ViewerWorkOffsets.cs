using System.Net.Http;
using System.Net.Http.Json;

namespace Meimad.Planner.Client.Windows.Api;

internal partial interface IPlannerApiClient
{
    /// <summary>The Operation's work offsets per NC viewer machine (schema v120): the one source the viewer places the program with.</summary>
    Task<IReadOnlyList<OperationViewerWorkOffsets>> ListOperationViewerWorkOffsetsAsync(
        string caseId, string caseOperationId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    /// <summary>Creates (entity tag null) or replaces the work offsets of one viewer machine; label → axis → mm/deg.</summary>
    Task<OperationViewerWorkOffsets> SaveOperationViewerWorkOffsetsAsync(
        string caseId, string caseOperationId, string viewerMachine,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> offsets, string? entityTag,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task DeleteOperationViewerWorkOffsetsAsync(
        string caseId, string caseOperationId, string viewerMachine, string entityTag, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

internal sealed partial class PlannerApiClient
{
    private static string ViewerWorkOffsetsPath(string caseId, string caseOperationId) =>
        $"api/v1/cases/{Uri.EscapeDataString(caseId)}/operations/{Uri.EscapeDataString(caseOperationId)}/viewer-work-offsets";

    public async Task<IReadOnlyList<OperationViewerWorkOffsets>> ListOperationViewerWorkOffsetsAsync(
        string caseId, string caseOperationId, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(ViewerWorkOffsetsPath(caseId, caseOperationId), cancellationToken);
        return (await ReadSuccessAsync<OperationViewerWorkOffsetsList>(response, cancellationToken)).Items ?? [];
    }

    public async Task<OperationViewerWorkOffsets> SaveOperationViewerWorkOffsetsAsync(
        string caseId, string caseOperationId, string viewerMachine,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> offsets, string? entityTag,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put,
            $"{ViewerWorkOffsetsPath(caseId, caseOperationId)}/{Uri.EscapeDataString(viewerMachine)}");
        if (entityTag is null) request.Headers.TryAddWithoutValidation("If-None-Match", "*");
        else request.Headers.TryAddWithoutValidation("If-Match", entityTag);
        request.Content = JsonContent.Create(new { offsets }, options: JsonOptions);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return await ReadSuccessAsync<OperationViewerWorkOffsets>(response, cancellationToken);
    }

    public async Task DeleteOperationViewerWorkOffsetsAsync(
        string caseId, string caseOperationId, string viewerMachine, string entityTag, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete,
            $"{ViewerWorkOffsetsPath(caseId, caseOperationId)}/{Uri.EscapeDataString(viewerMachine)}");
        request.Headers.TryAddWithoutValidation("If-Match", entityTag);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessWithoutBodyAsync(response, cancellationToken);
    }
}

/// <summary>
/// The work offsets of one Operation on one NC viewer machine: label (G54, G54.1 P3 ...) → axis letter → absolute machine
/// coordinate in mm or degrees, as the control holds it. <see cref="EntityTag"/> replaces or deletes exactly this version.
/// </summary>
internal sealed record OperationViewerWorkOffsets(
    string CaseOperationId,
    string ViewerMachine,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> Offsets,
    int Version,
    string EntityTag,
    DateTimeOffset UpdatedAt,
    string? UpdatedBy);

internal sealed record OperationViewerWorkOffsetsList(IReadOnlyList<OperationViewerWorkOffsets>? Items);
