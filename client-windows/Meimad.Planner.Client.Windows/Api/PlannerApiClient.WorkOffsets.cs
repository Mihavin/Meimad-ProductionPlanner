using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;

namespace Meimad.Planner.Client.Windows.Api;

/// <summary>Measured work offsets (schema v116, docs/api-contract.md "Measured work offsets").</summary>
internal partial interface IPlannerApiClient
{
    /// <summary>Current records and history of one Machine and Case Operation, optionally for one work offset.</summary>
    Task<WorkOffsetOperationInfo> GetOperationWorkOffsetsAsync(
        string machineId, string caseOperationId, string? workOffset = null, int? limit = null,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <summary>Measured work offsets by status (comma-separated), Machine and Case Operation, newest received first.</summary>
    Task<IReadOnlyList<WorkOffsetRecordInfo>> ListWorkOffsetsAsync(
        string? statuses = null, string? machineId = null, string? caseOperationId = null, int? limit = null,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <summary>Accepts or rejects a REVIEW_REQUIRED candidate; a stale decision is refused with 409.</summary>
    Task<WorkOffsetRecordInfo> ReviewWorkOffsetAsync(
        string workOffsetId, bool accept, WorkOffsetReviewRequestInfo request, string clientId,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <summary>The Machine's work offset report program for one Case Operation and NC release.</summary>
    Task<WorkOffsetReportInfo> GetWorkOffsetReportAsync(
        string machineId, string caseOperationId, string? releaseId, string? offsets, string? axes,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

internal sealed partial class PlannerApiClient
{
    public async Task<WorkOffsetOperationInfo> GetOperationWorkOffsetsAsync(
        string machineId, string caseOperationId, string? workOffset = null, int? limit = null,
        CancellationToken cancellationToken = default)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(workOffset)) query.Add("offset=" + Uri.EscapeDataString(workOffset.Trim()));
        if (limit is { } value) query.Add("limit=" + value.ToString(CultureInfo.InvariantCulture));
        using var response = await httpClient.GetAsync(
            $"api/v1/machines/{Uri.EscapeDataString(machineId)}/operations/{Uri.EscapeDataString(caseOperationId)}/work-offsets"
            + (query.Count == 0 ? string.Empty : "?" + string.Join("&", query)),
            cancellationToken);
        return await ReadSuccessAsync<WorkOffsetOperationInfo>(response, cancellationToken);
    }

    public async Task<IReadOnlyList<WorkOffsetRecordInfo>> ListWorkOffsetsAsync(
        string? statuses = null, string? machineId = null, string? caseOperationId = null, int? limit = null,
        CancellationToken cancellationToken = default)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(statuses)) query.Add("status=" + Uri.EscapeDataString(statuses.Trim()));
        if (!string.IsNullOrWhiteSpace(machineId)) query.Add("machineId=" + Uri.EscapeDataString(machineId.Trim()));
        if (!string.IsNullOrWhiteSpace(caseOperationId)) query.Add("caseOperationId=" + Uri.EscapeDataString(caseOperationId.Trim()));
        if (limit is { } value) query.Add("limit=" + value.ToString(CultureInfo.InvariantCulture));
        using var response = await httpClient.GetAsync(
            "api/v1/machine-evidence/work-offsets" + (query.Count == 0 ? string.Empty : "?" + string.Join("&", query)),
            cancellationToken);
        return (await ReadSuccessAsync<WorkOffsetListInfo>(response, cancellationToken)).Items;
    }

    public async Task<WorkOffsetRecordInfo> ReviewWorkOffsetAsync(
        string workOffsetId, bool accept, WorkOffsetReviewRequestInfo request, string clientId,
        CancellationToken cancellationToken = default)
    {
        using var message = CreateRequest(
            HttpMethod.Post,
            $"api/v1/machine-evidence/work-offsets/{Uri.EscapeDataString(workOffsetId)}/{(accept ? "accept" : "reject")}",
            clientId);
        message.Content = JsonContent.Create(request, options: JsonOptions);
        using var response = await httpClient.SendAsync(message, cancellationToken);
        return await ReadSuccessAsync<WorkOffsetRecordInfo>(response, cancellationToken);
    }

    public async Task<WorkOffsetReportInfo> GetWorkOffsetReportAsync(
        string machineId, string caseOperationId, string? releaseId, string? offsets, string? axes,
        CancellationToken cancellationToken = default)
    {
        var query = new List<string> { "operationId=" + Uri.EscapeDataString(caseOperationId) };
        if (!string.IsNullOrWhiteSpace(releaseId)) query.Add("releaseId=" + Uri.EscapeDataString(releaseId));
        if (!string.IsNullOrWhiteSpace(offsets)) query.Add("offsets=" + Uri.EscapeDataString(offsets.Trim()));
        if (!string.IsNullOrWhiteSpace(axes)) query.Add("axes=" + Uri.EscapeDataString(axes.Trim()));
        using var response = await httpClient.GetAsync(
            $"api/v1/machines/{Uri.EscapeDataString(machineId)}/work-offset-report?{string.Join("&", query)}",
            cancellationToken);
        return await ReadSuccessAsync<WorkOffsetReportInfo>(response, cancellationToken);
    }
}
