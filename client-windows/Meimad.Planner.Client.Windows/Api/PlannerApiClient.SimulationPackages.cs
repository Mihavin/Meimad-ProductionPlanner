using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace Meimad.Planner.Client.Windows.Api;

internal partial interface IPlannerApiClient
{
    /// <summary>Imports a Cimatron simulation package into the Operation; a blocked import returns its report.</summary>
    Task<SimulationPackageImportResult> ImportSimulationPackageAsync(string caseId, string caseOperationId, SimulationPackageImportRequest request, string clientId, long editGeneration, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    /// <summary>The Operation's package imports, newest first (the current one has status "imported").</summary>
    Task<IReadOnlyList<SimulationPackageImport>> ListSimulationPackageImportsAsync(string caseId, string caseOperationId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

internal sealed partial class PlannerApiClient
{
    private sealed record SimulationPackageImportEnvelope(string? Outcome, SimulationPackageImport? Import, SimulationPackageReport? Report);

    private sealed record SimulationPackageBlockedEnvelope(SimulationPackageReport? Report);

    private sealed record SimulationPackageImportList(IReadOnlyList<SimulationPackageImport>? Items);

    private static string SimulationPackagesPath(string caseId, string caseOperationId) =>
        $"api/v1/cases/{Uri.EscapeDataString(caseId)}/operations/{Uri.EscapeDataString(caseOperationId)}/simulation-packages";

    public async Task<SimulationPackageImportResult> ImportSimulationPackageAsync(
        string caseId, string caseOperationId, SimulationPackageImportRequest request, string clientId, long editGeneration,
        CancellationToken cancellationToken = default)
    {
        using var message = CreateRequest(HttpMethod.Post, SimulationPackagesPath(caseId, caseOperationId), clientId);
        message.Headers.Add(EditGenerationHeader, editGeneration.ToString(CultureInfo.InvariantCulture));
        message.Content = JsonContent.Create(request, options: JsonOptions);
        // The Server hashes and reads every package file, so the import uses the long-timeout client.
        using var response = await importHttpClient.SendAsync(message, cancellationToken);
        if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            var payload = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            SimulationPackageBlockedEnvelope? blocked = null;
            try
            {
                blocked = JsonSerializer.Deserialize<SimulationPackageBlockedEnvelope>(payload, JsonOptions);
            }
            catch (JsonException)
            {
                // Not a package report: reported as the plain error below.
            }

            if (blocked?.Report is { } report) return new SimulationPackageImportResult(SimulationPackageImportResult.Blocked, null, report);
            ThrowApiError(response, payload);
        }

        var envelope = await ReadSuccessAsync<SimulationPackageImportEnvelope>(response, cancellationToken);
        if (envelope.Report is null || string.IsNullOrWhiteSpace(envelope.Outcome))
            throw new PlannerProtocolException("Server returned an incomplete package import response.");
        return new SimulationPackageImportResult(envelope.Outcome, envelope.Import, envelope.Report);
    }

    public async Task<IReadOnlyList<SimulationPackageImport>> ListSimulationPackageImportsAsync(
        string caseId, string caseOperationId, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(SimulationPackagesPath(caseId, caseOperationId), cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound && response.Content.Headers.ContentLength is null or 0)
            throw new NotSupportedException("This Server does not support Cimatron packages.");
        return (await ReadSuccessAsync<SimulationPackageImportList>(response, cancellationToken)).Items ?? [];
    }
}
