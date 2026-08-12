using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace Meimad.Planner.Client.Windows.Api;

internal interface IPlannerApiClient : IDisposable
{
    Task<ServerHealth> GetHealthAsync(CancellationToken cancellationToken = default);

    Task<EditModeStatus> GetEditModeAsync(
        string clientId,
        CancellationToken cancellationToken = default);

    Task<EditModeStatus> RequestEditAsync(
        string clientId,
        string userId,
        CancellationToken cancellationToken = default);

    Task<EditModeStatus> ReleaseEditAsync(
        string clientId,
        long generation,
        CancellationToken cancellationToken = default);

    Task<EditModeStatus> DecideTransferAsync(
        string clientId,
        long generation,
        string requestId,
        bool release,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PlannerCase>> ListCasesAsync(
        CaseQuery query,
        CancellationToken cancellationToken = default);

    Task<CaseResource> GetCaseAsync(
        string caseId,
        CancellationToken cancellationToken = default);

    Task<CaseResource> CreateCaseAsync(
        CaseUpdate create,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task<CaseResource> UpdateCaseAsync(
        string caseId,
        CaseUpdate update,
        string entityTag,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CaseOperation>> ListCaseOperationsAsync(
        string caseId,
        CancellationToken cancellationToken = default);

    Task<CaseOperation> CreateCaseOperationAsync(
        string caseId,
        CaseOperationCreate create,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task<CaseOperation> UpdateCaseOperationAsync(
        string caseId,
        string operationId,
        CaseOperationUpdate update,
        string entityTag,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task<IReadOnlyList<PlannerOrder>> ListOrdersAsync(
        string caseId,
        CancellationToken cancellationToken = default);

    Task<PlannerOrder> CreateOrderAsync(
        OrderCreate create,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task<IReadOnlyList<ProductionBatch>> ListBatchesAsync(
        string caseId,
        CancellationToken cancellationToken = default);

    Task<ProductionBatch> CreateBatchAsync(
        ProductionBatchCreate create,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task<byte[]?> GetCasePreviewAsync(
        string caseId,
        CancellationToken cancellationToken = default);

    Task<PlannerMachine> CreateMachineAsync(
        MachineCreate create,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task<IReadOnlyList<PlannerMachine>> ListMachinesAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PlannerMachine>>([]);

    Task<MachineResource> GetMachineAsync(string machineId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task<MachineResource> UpdateMachineAsync(
        string machineId, MachineCreate update, string entityTag,
        string clientId, long editGeneration, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task DeleteCaseAsync(string caseId, string clientId, long editGeneration, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task DeleteCaseOperationAsync(string caseId, string operationId, string clientId, long editGeneration, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task DeleteOrderAsync(string orderId, string clientId, long editGeneration, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task DeleteBatchAsync(string batchId, string clientId, long editGeneration, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task DeleteMachineAsync(string machineId, string clientId, long editGeneration, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    Task<IReadOnlyList<WorkingCalendar>> ListWorkingCalendarsAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<WorkingCalendar>>([]);

    Task<WorkingCalendar> CreateWorkingCalendarAsync(
        WorkingCalendarCreate create,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    Task<byte[]?> GetMachinePictureAsync(
        string machineId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<byte[]?>(null);

    Task<PlanningBoardSnapshot> GetPlanningBoardAsync(
        CancellationToken cancellationToken = default);

    Task<TimelineSnapshot> GetTimelineAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default);

    Task AssignOrMoveOperationAsync(
        string batchOperationId,
        string machineId,
        int backlogPosition,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default);

    Task UnassignOperationAsync(
        string batchOperationId,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default);

    Task<BatchOperationExecution> ChangeOperationExecutionAsync(
        string batchOperationId,
        string action,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

internal interface IPlannerApiClientFactory
{
    IPlannerApiClient Create(Uri serverBaseUri);
}

internal sealed class PlannerApiClientFactory : IPlannerApiClientFactory
{
    public IPlannerApiClient Create(Uri serverBaseUri) => new PlannerApiClient(
        new HttpClient
        {
            BaseAddress = serverBaseUri,
            Timeout = TimeSpan.FromSeconds(10)
        });
}

internal sealed class PlannerApiClient : IPlannerApiClient
{
    private const string ClientIdHeader = "X-Meimad-Client-Id";
    private const string UserIdHeader = "X-Meimad-User-Id";
    private const string EditGenerationHeader = "X-Meimad-Edit-Generation";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient httpClient;

    internal PlannerApiClient(HttpClient httpClient)
    {
        this.httpClient = httpClient;
    }

    public async Task<ServerHealth> GetHealthAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync("health", cancellationToken);
        var dto = await ReadSuccessAsync<ServerHealthDto>(response, cancellationToken);
        return new ServerHealth(
            Required(dto.Status, "health status"),
            Required(dto.Service, "service name"),
            Required(dto.Version, "service version"),
            dto.ServerTimeUtc);
    }

    public async Task<EditModeStatus> GetEditModeAsync(
        string clientId,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, "api/v1/edit-mode", clientId);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return Map(await ReadSuccessAsync<EditModeDto>(response, cancellationToken));
    }

    public async Task<EditModeStatus> RequestEditAsync(
        string clientId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, "api/v1/edit-mode/requests", clientId);
        request.Headers.Add(UserIdHeader, userId);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return Map(await ReadSuccessAsync<EditModeDto>(response, cancellationToken));
    }

    public async Task<EditModeStatus> ReleaseEditAsync(
        string clientId,
        long generation,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, "api/v1/edit-mode/release", clientId);
        request.Headers.Add(
            EditGenerationHeader,
            generation.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return Map(await ReadSuccessAsync<EditModeDto>(response, cancellationToken));
    }

    public async Task<EditModeStatus> DecideTransferAsync(
        string clientId,
        long generation,
        string requestId,
        bool release,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(
            HttpMethod.Post,
            $"api/v1/edit-mode/requests/{Uri.EscapeDataString(requestId)}/decision",
            clientId);
        request.Headers.Add(
            EditGenerationHeader,
            generation.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = JsonContent.Create(new
        {
            decision = release ? "release" : "reject"
        });
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return Map(await ReadSuccessAsync<EditModeDto>(response, cancellationToken));
    }

    public async Task<IReadOnlyList<PlannerCase>> ListCasesAsync(
        CaseQuery query,
        CancellationToken cancellationToken = default)
    {
        var parameters = new List<string>();
        AddQueryParameter(parameters, "search", query.Search);
        AddQueryParameter(parameters, "customer", query.Customer);
        if (query.IsActive.HasValue)
        {
            parameters.Add($"isActive={query.IsActive.Value.ToString().ToLowerInvariant()}");
        }

        var path = "api/v1/cases" + (parameters.Count == 0 ? string.Empty : "?" + string.Join("&", parameters));
        using var response = await httpClient.GetAsync(path, cancellationToken);
        return (await ReadSuccessAsync<ListResponse<PlannerCase>>(response, cancellationToken)).Items;
    }

    public async Task<CaseResource> GetCaseAsync(
        string caseId,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(
            $"api/v1/cases/{Uri.EscapeDataString(caseId)}",
            cancellationToken);
        var value = await ReadSuccessAsync<PlannerCase>(response, cancellationToken);
        return new CaseResource(value, RequiredEntityTag(response));
    }

    public async Task<CaseResource> CreateCaseAsync(
        CaseUpdate create,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, "api/v1/cases", clientId);
        request.Headers.Add(
            EditGenerationHeader,
            editGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = JsonContent.Create(create);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var value = await ReadSuccessAsync<PlannerCase>(response, cancellationToken);
        return new CaseResource(value, RequiredEntityTag(response));
    }

    public async Task<CaseResource> UpdateCaseAsync(
        string caseId,
        CaseUpdate update,
        string entityTag,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(
            HttpMethod.Patch,
            $"api/v1/cases/{Uri.EscapeDataString(caseId)}",
            clientId);
        request.Headers.TryAddWithoutValidation("If-Match", entityTag);
        request.Headers.Add(
            EditGenerationHeader,
            editGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = JsonContent.Create(update);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var value = await ReadSuccessAsync<PlannerCase>(response, cancellationToken);
        return new CaseResource(value, RequiredEntityTag(response));
    }

    public async Task<IReadOnlyList<CaseOperation>> ListCaseOperationsAsync(
        string caseId,
        CancellationToken cancellationToken = default) =>
        await ReadListAsync<CaseOperation>(
            $"api/v1/cases/{Uri.EscapeDataString(caseId)}/operations",
            cancellationToken);

    public async Task<CaseOperation> CreateCaseOperationAsync(
        string caseId,
        CaseOperationCreate create,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(
            HttpMethod.Post,
            $"api/v1/cases/{Uri.EscapeDataString(caseId)}/operations",
            clientId);
        request.Headers.Add(
            EditGenerationHeader,
            editGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = JsonContent.Create(create);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return await ReadSuccessAsync<CaseOperation>(response, cancellationToken);
    }

    public async Task<CaseOperation> UpdateCaseOperationAsync(
        string caseId,
        string operationId,
        CaseOperationUpdate update,
        string entityTag,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(
            HttpMethod.Patch,
            $"api/v1/cases/{Uri.EscapeDataString(caseId)}/operations/{Uri.EscapeDataString(operationId)}",
            clientId);
        request.Headers.TryAddWithoutValidation("If-Match", entityTag);
        request.Headers.Add(
            EditGenerationHeader,
            editGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = JsonContent.Create(update);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return await ReadSuccessAsync<CaseOperation>(response, cancellationToken);
    }

    public async Task<IReadOnlyList<PlannerOrder>> ListOrdersAsync(
        string caseId,
        CancellationToken cancellationToken = default) =>
        await ReadListAsync<PlannerOrder>(
            $"api/v1/orders?caseId={Uri.EscapeDataString(caseId)}",
            cancellationToken);

    public async Task<PlannerOrder> CreateOrderAsync(
        OrderCreate create,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, "api/v1/orders", clientId);
        request.Headers.Add(
            EditGenerationHeader,
            editGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = JsonContent.Create(create);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return await ReadSuccessAsync<PlannerOrder>(response, cancellationToken);
    }

    public async Task<IReadOnlyList<ProductionBatch>> ListBatchesAsync(
        string caseId,
        CancellationToken cancellationToken = default) =>
        await ReadListAsync<ProductionBatch>(
            $"api/v1/batches?caseId={Uri.EscapeDataString(caseId)}",
            cancellationToken);

    public async Task<ProductionBatch> CreateBatchAsync(
        ProductionBatchCreate create,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, "api/v1/batches", clientId);
        request.Headers.Add(
            EditGenerationHeader,
            editGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = JsonContent.Create(create);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return await ReadSuccessAsync<ProductionBatch>(response, cancellationToken);
    }

    public async Task<byte[]?> GetCasePreviewAsync(
        string caseId,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(
            $"api/v1/cases/{Uri.EscapeDataString(caseId)}/preview",
            cancellationToken);
        if (response.StatusCode is System.Net.HttpStatusCode.NotFound
            or System.Net.HttpStatusCode.UnsupportedMediaType)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            await ThrowApiErrorAsync(response, cancellationToken);
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    public async Task<PlannerMachine> CreateMachineAsync(
        MachineCreate create,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, "api/v1/machines", clientId);
        request.Headers.Add(
            EditGenerationHeader,
            editGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = JsonContent.Create(create);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return await ReadSuccessAsync<PlannerMachine>(response, cancellationToken);
    }

    public async Task<IReadOnlyList<PlannerMachine>> ListMachinesAsync(
        CancellationToken cancellationToken = default) =>
        await ReadListAsync<PlannerMachine>("api/v1/machines", cancellationToken);

    public async Task<MachineResource> GetMachineAsync(
        string machineId,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync($"api/v1/machines/{Uri.EscapeDataString(machineId)}", cancellationToken);
        return new MachineResource(
            await ReadSuccessAsync<PlannerMachine>(response, cancellationToken),
            RequiredEntityTag(response));
    }

    public async Task<MachineResource> UpdateMachineAsync(
        string machineId,
        MachineCreate update,
        string entityTag,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Patch, $"api/v1/machines/{Uri.EscapeDataString(machineId)}", clientId);
        request.Headers.TryAddWithoutValidation("If-Match", entityTag);
        request.Headers.Add(EditGenerationHeader, editGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = JsonContent.Create(update);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return new MachineResource(
            await ReadSuccessAsync<PlannerMachine>(response, cancellationToken),
            RequiredEntityTag(response));
    }

    public Task DeleteCaseAsync(string caseId, string clientId, long generation, CancellationToken token = default) =>
        DeleteAsync($"api/v1/cases/{Uri.EscapeDataString(caseId)}", clientId, generation, token);
    public Task DeleteCaseOperationAsync(string caseId, string operationId, string clientId, long generation, CancellationToken token = default) =>
        DeleteAsync($"api/v1/cases/{Uri.EscapeDataString(caseId)}/operations/{Uri.EscapeDataString(operationId)}", clientId, generation, token);
    public Task DeleteOrderAsync(string orderId, string clientId, long generation, CancellationToken token = default) =>
        DeleteAsync($"api/v1/orders/{Uri.EscapeDataString(orderId)}", clientId, generation, token);
    public Task DeleteBatchAsync(string batchId, string clientId, long generation, CancellationToken token = default) =>
        DeleteAsync($"api/v1/batches/{Uri.EscapeDataString(batchId)}", clientId, generation, token);
    public Task DeleteMachineAsync(string machineId, string clientId, long generation, CancellationToken token = default) =>
        DeleteAsync($"api/v1/machines/{Uri.EscapeDataString(machineId)}", clientId, generation, token);

    private async Task DeleteAsync(string path, string clientId, long generation, CancellationToken token)
    {
        using var request = CreateRequest(HttpMethod.Delete, path, clientId);
        request.Headers.Add(EditGenerationHeader, generation.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var response = await httpClient.SendAsync(request, token);
        await EnsureSuccessWithoutBodyAsync(response, token);
    }

    public async Task<IReadOnlyList<WorkingCalendar>> ListWorkingCalendarsAsync(
        CancellationToken cancellationToken = default) =>
        await ReadListAsync<WorkingCalendar>("api/v1/working-calendars", cancellationToken);

    public async Task<WorkingCalendar> CreateWorkingCalendarAsync(
        WorkingCalendarCreate create,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, "api/v1/working-calendars", clientId);
        request.Headers.Add(
            EditGenerationHeader,
            editGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = JsonContent.Create(create);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return await ReadSuccessAsync<WorkingCalendar>(response, cancellationToken);
    }

    public async Task<byte[]?> GetMachinePictureAsync(
        string machineId,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(
            $"api/v1/machines/{Uri.EscapeDataString(machineId)}/picture",
            cancellationToken);
        if (response.StatusCode is System.Net.HttpStatusCode.NotFound
            or System.Net.HttpStatusCode.UnsupportedMediaType)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            await ThrowApiErrorAsync(response, cancellationToken);
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    public async Task<PlanningBoardSnapshot> GetPlanningBoardAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync("api/v1/planning-board", cancellationToken);
        return await ReadSuccessAsync<PlanningBoardSnapshot>(response, cancellationToken);
    }

    public async Task<TimelineSnapshot> GetTimelineAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default)
    {
        var fromValue = Uri.EscapeDataString(from.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        var toValue = Uri.EscapeDataString(to.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        using var response = await httpClient.GetAsync(
            $"api/v1/timeline?from={fromValue}&to={toValue}",
            cancellationToken);
        return await ReadSuccessAsync<TimelineSnapshot>(response, cancellationToken);
    }

    public async Task AssignOrMoveOperationAsync(
        string batchOperationId,
        string machineId,
        int backlogPosition,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(
            HttpMethod.Put,
            $"api/v1/batch-operations/{Uri.EscapeDataString(batchOperationId)}/assignment",
            clientId);
        request.Headers.Add(
            EditGenerationHeader,
            editGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = JsonContent.Create(new { machineId, backlogPosition });
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessWithoutBodyAsync(response, cancellationToken);
    }

    public async Task UnassignOperationAsync(
        string batchOperationId,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(
            HttpMethod.Delete,
            $"api/v1/batch-operations/{Uri.EscapeDataString(batchOperationId)}/assignment",
            clientId);
        request.Headers.Add(
            EditGenerationHeader,
            editGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessWithoutBodyAsync(response, cancellationToken);
    }

    public async Task<BatchOperationExecution> ChangeOperationExecutionAsync(
        string batchOperationId,
        string action,
        string clientId,
        long editGeneration,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(
            HttpMethod.Post,
            $"api/v1/batch-operations/{Uri.EscapeDataString(batchOperationId)}/{Uri.EscapeDataString(action)}",
            clientId);
        request.Headers.Add(
            EditGenerationHeader,
            editGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return await ReadSuccessAsync<BatchOperationExecution>(response, cancellationToken);
    }

    public void Dispose()
    {
        httpClient.Dispose();
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        string path,
        string clientId)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add(ClientIdHeader, clientId);
        return request;
    }

    private static async Task<T> ReadSuccessAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            await ThrowApiErrorAsync(response, cancellationToken);
        }

        try
        {
            return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken)
                ?? throw new PlannerProtocolException("Server returned an empty response.");
        }
        catch (JsonException exception)
        {
            throw new PlannerProtocolException(
                $"Server returned an invalid {typeof(T).Name} response: {exception.Message}");
        }
    }

    private async Task<IReadOnlyList<T>> ReadListAsync<T>(
        string path,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(path, cancellationToken);
        return (await ReadSuccessAsync<ListResponse<T>>(response, cancellationToken)).Items;
    }

    private static async Task EnsureSuccessWithoutBodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            await ThrowApiErrorAsync(response, cancellationToken);
        }
    }

    private static async Task ThrowApiErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        ErrorEnvelope? error = null;
        try
        {
            error = await response.Content.ReadFromJsonAsync<ErrorEnvelope>(
                JsonOptions,
                cancellationToken);
        }
        catch (JsonException)
        {
            // The safe fallback below avoids showing raw server content.
        }

        throw new PlannerApiException(
            response.StatusCode,
            error?.Error?.Code ?? "server_error",
            error?.Error?.Message ?? $"Server returned HTTP {(int)response.StatusCode}.");
    }

    private static void AddQueryParameter(List<string> parameters, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parameters.Add($"{name}={Uri.EscapeDataString(value.Trim())}");
        }
    }

    private static string RequiredEntityTag(HttpResponseMessage response) =>
        response.Headers.ETag?.ToString()
        ?? throw new PlannerProtocolException("Server response is missing the resource ETag.");

    private static EditModeStatus Map(EditModeDto dto)
    {
        var state = dto.State switch
        {
            "viewer" => ClientEditState.Viewer,
            "editor" => ClientEditState.Editor,
            "requestingEdit" => ClientEditState.RequestingEdit,
            _ => throw new PlannerProtocolException(
                $"Server returned unknown Edit Mode state '{dto.State ?? "<null>"}'.")
        };
        var holder = dto.Holder is null
            ? null
            : new EditModeHolder(
                Required(dto.Holder.ClientId, "holder client ID"),
                Required(dto.Holder.UserId, "holder user ID"),
                dto.Holder.Generation,
                dto.Holder.AcquiredAt);
        var pending = dto.PendingRequest is null
            ? null
            : new EditTransferRequest(
                Required(dto.PendingRequest.RequestId, "request ID"),
                Required(dto.PendingRequest.RequesterClientId, "requester client ID"),
                Required(dto.PendingRequest.RequesterUserId, "requester user ID"),
                Required(dto.PendingRequest.Status, "request status"),
                dto.PendingRequest.RequestedAt,
                dto.PendingRequest.DecisionDeadline);
        return new EditModeStatus(
            state,
            dto.Generation,
            holder,
            pending,
            dto.ServerTime,
            dto.TransferTimeoutSeconds);
    }

    private static string Required(string? value, string field)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new PlannerProtocolException($"Server response is missing {field}.")
            : value;
    }

    private sealed record ServerHealthDto(
        string? Status,
        string? Service,
        string? Version,
        DateTimeOffset ServerTimeUtc);

    private sealed record EditModeDto(
        string? State,
        long Generation,
        EditModeHolderDto? Holder,
        EditTransferRequestDto? PendingRequest,
        DateTimeOffset ServerTime,
        int TransferTimeoutSeconds);

    private sealed record EditModeHolderDto(
        string? ClientId,
        string? UserId,
        long Generation,
        DateTimeOffset AcquiredAt);

    private sealed record EditTransferRequestDto(
        string? RequestId,
        string? RequesterClientId,
        string? RequesterUserId,
        string? Status,
        DateTimeOffset RequestedAt,
        DateTimeOffset DecisionDeadline);

    private sealed record ErrorEnvelope(ErrorBody? Error);

    private sealed record ErrorBody(string? Code, string? Message);

    private sealed record ListResponse<T>(IReadOnlyList<T> Items, string? NextCursor);
}
