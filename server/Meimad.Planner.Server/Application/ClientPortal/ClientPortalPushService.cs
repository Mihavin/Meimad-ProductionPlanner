using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Meimad.Planner.Server.Application.Cases;
using Meimad.Planner.Server.Application.Orders;
using Meimad.Planner.Server.Configuration;
using Meimad.Planner.Server.Domain.ClientPortal;
using Meimad.Planner.Server.Domain.Orders;

namespace Meimad.Planner.Server.Application.ClientPortal;

/// <summary>
/// Pushes each configured customer's current Orders to the cloud customer portal's
/// ingest endpoint. This is the Server-hosted replacement for the portal project's
/// stand-alone <c>onprem-agent</c>: it reads Cases and Orders straight from the
/// repositories (the same rows the API would return) and sends only the
/// customer-safe fields. It never reads Machines, Batches, Setup, or Edit Mode state,
/// never writes anything locally, and holds no cloud credential beyond the ingest
/// shared secret.
/// The customer mapping is read from the database on every cycle, so a mapping added or
/// removed from Setup takes effect on the next cycle without restarting the Server.
/// </summary>
internal sealed class ClientPortalPushService(
    ClientPortalOptions options,
    IClientPortalCustomerRepository customers,
    ICaseRepository cases,
    IOrderRepository orders,
    HttpClient httpClient,
    ILogger<ClientPortalPushService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal sealed record PortalOrder(
        string OrderNumber,
        string PartNumber,
        string Description,
        int Quantity,
        string WorkFinishDate,
        string Status,
        string UpdatedAt);

    internal sealed record CustomerPushResult(
        string CustomerId,
        int OrderCount,
        bool Succeeded,
        string? Error,
        int? OrdersWritten = null,
        int? OrdersRemoved = null);

    /// <summary>Collects the customer-safe Orders for one exact customer name.</summary>
    internal async Task<IReadOnlyList<PortalOrder>> CollectAsync(string customer, CancellationToken cancellationToken)
    {
        var needle = customer.Trim();
        // The repository filter is a substring match (same as GET /api/v1/cases?customer=);
        // keep only an exact, case-insensitive match so a same-substring different
        // customer is never pushed under this customer id.
        var matchingCases = (await cases.ListAsync(null, needle, null, CaseSortOrder.PartNumber, cancellationToken))
            .Where(c => string.Equals((c.Customer ?? string.Empty).Trim(), needle, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var result = new List<PortalOrder>();
        foreach (var plannerCase in matchingCases)
        {
            foreach (var order in await orders.ListByCaseAsync(plannerCase.CaseId, cancellationToken))
            {
                result.Add(new PortalOrder(
                    order.OrderNumber,
                    plannerCase.PartNumber,
                    plannerCase.Name,
                    order.Quantity,
                    order.WorkFinishDate.ToString("yyyy-MM-dd"),
                    order.EffectiveStatusToken(),
                    order.UpdatedAt.ToUniversalTime().ToString("O")));
            }
        }

        return result;
    }

    internal async Task<CustomerPushResult> PushCustomerAsync(ClientPortalCustomer customer, CancellationToken cancellationToken)
    {
        IReadOnlyList<PortalOrder> collected;
        try
        {
            collected = await CollectAsync(customer.Customer, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Client portal: reading Orders for customer {CustomerId} failed.", customer.CustomerId);
            return new CustomerPushResult(customer.CustomerId, 0, false, exception.Message);
        }

        var payload = new { customerId = customer.CustomerId, orders = collected };
        using var request = new HttpRequestMessage(HttpMethod.Post, options.IngestUrl)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ResolvedSharedSecret);

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Client portal: ingest rejected customer {CustomerId} with HTTP {Status}: {Body}",
                    customer.CustomerId, (int)response.StatusCode, Truncate(body));
                return new CustomerPushResult(customer.CustomerId, collected.Count, false, $"HTTP {(int)response.StatusCode}: {Truncate(body)}");
            }

            int? written = null, removed = null;
            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.TryGetProperty("ordersWritten", out var w)) written = w.GetInt32();
                if (document.RootElement.TryGetProperty("ordersRemoved", out var r)) removed = r.GetInt32();
            }
            catch (JsonException)
            {
                // A success without the expected body is still a success; counts are informational.
            }

            logger.LogInformation(
                "Client portal: pushed {Count} Order(s) for customer {CustomerId} ({Written} written, {Removed} removed).",
                collected.Count, customer.CustomerId, written, removed);
            return new CustomerPushResult(customer.CustomerId, collected.Count, true, null, written, removed);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Client portal: push for customer {CustomerId} failed.", customer.CustomerId);
            return new CustomerPushResult(customer.CustomerId, collected.Count, false, exception.Message);
        }
    }

    internal async Task<IReadOnlyList<CustomerPushResult>> PushAllAsync(CancellationToken cancellationToken)
    {
        var mapped = await customers.ListAsync(cancellationToken);
        var results = new List<CustomerPushResult>(mapped.Count);
        foreach (var customer in mapped)
        {
            results.Add(await PushCustomerAsync(customer, cancellationToken));
        }

        return results;
    }

    private static string Truncate(string value) => value.Length <= 300 ? value : value[..300] + "…";
}

internal sealed class ClientPortalPushHostedService(
    ClientPortalOptions options,
    ClientPortalPushService service,
    ILogger<ClientPortalPushHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            logger.LogInformation("Client portal push is disabled.");
            return;
        }

        // The mapped customers are read per cycle, not here: Setup can add the first one
        // (or remove the last one) while the Server runs, and a cycle with none is a no-op.
        logger.LogInformation(
            "Client portal push enabled: pushing mapped customer(s) to {Url} every {Seconds}s.",
            options.IngestUrl, options.PollIntervalSeconds);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.PollIntervalSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await service.PushAllAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Client portal push cycle failed.");
            }

            if (!await timer.WaitForNextTickAsync(stoppingToken))
            {
                break;
            }
        }
    }
}
