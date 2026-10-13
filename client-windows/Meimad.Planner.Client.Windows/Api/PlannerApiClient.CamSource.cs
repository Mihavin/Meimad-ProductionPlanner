using System.Net;
using System.Net.Http;

namespace Meimad.Planner.Client.Windows.Api;

internal partial interface IPlannerApiClient
{
    /// <summary>The Operation's CAM source (the Cimatron document of its last Import from Cimatron), or null when it has none.</summary>
    Task<OperationCamSourceResource?> GetOperationCamSourceAsync(string caseId, string caseOperationId, CancellationToken cancellationToken = default) =>
        Task.FromResult<OperationCamSourceResource?>(null);
}

internal sealed partial class PlannerApiClient
{
    public async Task<OperationCamSourceResource?> GetOperationCamSourceAsync(string caseId, string caseOperationId,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(
            $"api/v1/cases/{Uri.EscapeDataString(caseId)}/operations/{Uri.EscapeDataString(caseOperationId)}/cam-source", cancellationToken);
        // 404: never imported from Cimatron (or a Server without the endpoint).
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        return await ReadSuccessAsync<OperationCamSourceResource>(response, cancellationToken);
    }
}

/// <summary>One CAM operation of an import: a whole Cimatron toolpath or one procedure, by Cimatron id.</summary>
internal sealed record CamOperationReference(string Kind, int Id, string? Name, string? Toolpath);

/// <summary>
/// GET …/operations/{id}/cam-source: the Cimatron document and CAM operations of the Operation's last Import from Cimatron.
/// <see cref="Shared"/> is false for a local path, which opens only on <see cref="SourceHost"/>.
/// </summary>
internal sealed record OperationCamSourceResource(
    string CaseOperationId,
    string Application,
    string DocumentPath,
    bool Shared,
    string? DocumentName,
    string? DocumentId,
    string? Setup,
    IReadOnlyList<CamOperationReference>? Operations,
    string? Postprocessor,
    string? ClientImportId,
    string SimulationPackageImportId,
    string? SourceHost,
    DateTimeOffset ImportedAt,
    string? ImportedBy,
    int Version,
    string? PackagePath,
    string? NcFileName,
    string PackageStatus);
