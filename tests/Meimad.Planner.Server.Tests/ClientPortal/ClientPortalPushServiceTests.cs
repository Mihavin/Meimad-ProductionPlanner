using System.Net;
using System.Text.Json;
using Meimad.Planner.Server.Application.ClientPortal;
using Meimad.Planner.Server.Configuration;
using Meimad.Planner.Server.Persistence;
using Meimad.Planner.Server.Tests.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meimad.Planner.Server.Tests.ClientPortal;

public sealed class ClientPortalPushServiceTests
{
    [Fact]
    public async Task Pushes_only_the_exact_customer_with_customer_safe_fields_and_the_bearer_secret()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await SeedAsync(fixture.Database);
        await MapAsync(fixture.Database, ("Acme Fabrication Ltd", "acme"));
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """{"ordersWritten":2,"ordersRemoved":1}"""));
        var service = Build(fixture.Database, handler, Options());

        var results = await service.PushAllAsync(CancellationToken.None);

        var result = Assert.Single(results);
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(2, result.OrderCount);
        Assert.Equal(2, result.OrdersWritten);
        Assert.Equal(1, result.OrdersRemoved);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://ingest.example/ingest/orders", request.Url);
        Assert.Equal("Bearer s3cret", request.Authorization);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("acme", body.RootElement.GetProperty("customerId").GetString());
        var orders = body.RootElement.GetProperty("orders").EnumerateArray().ToList();
        Assert.Equal(2, orders.Count);
        var first = orders.Single(o => o.GetProperty("orderNumber").GetString() == "E000374633/34980");
        Assert.Equal("1036U586-002", first.GetProperty("partNumber").GetString());
        Assert.Equal("RIGHT SLOPED LOCKING PLATE", first.GetProperty("description").GetString());
        Assert.Equal(6, first.GetProperty("quantity").GetInt32());
        Assert.Equal("2022-12-08", first.GetProperty("workFinishDate").GetString());
        Assert.Equal("in_production", first.GetProperty("status").GetString());
        Assert.DoesNotContain(orders, o => o.GetProperty("orderNumber").GetString() == "WO-OTHER");
        // Only the customer-safe field set, nothing else.
        Assert.Equal(
            new[] { "orderNumber", "partNumber", "description", "quantity", "workFinishDate", "status", "updatedAt" },
            first.EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Fact]
    public async Task A_rejected_push_is_reported_not_thrown_and_other_customers_still_go()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await SeedAsync(fixture.Database);
        var calls = 0;
        var handler = new StubHandler(_ => ++calls == 1
            ? Json(HttpStatusCode.NotFound, """{"error":{"code":"unknown_customer"}}""")
            : Json(HttpStatusCode.OK, """{"ordersWritten":0,"ordersRemoved":0}"""));
        await MapAsync(fixture.Database, ("Acme Fabrication Ltd", "acme"), ("Nobody Co", "nobody"));
        var service = Build(fixture.Database, handler, Options());

        var results = await service.PushAllAsync(CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.False(results[0].Succeeded);
        Assert.Contains("unknown_customer", results[0].Error);
        Assert.True(results[1].Succeeded);
        Assert.Equal(0, results[1].OrderCount);
    }

    [Fact]
    public void Disabled_by_default_and_validated_when_enabled()
    {
        Assert.False(ClientPortalOptions.FromConfiguration(new ConfigurationBuilder().Build(), Path.GetTempPath()).Enabled);

        var missingSecret = Configuration(("ClientPortal:Enabled", "true"), ("ClientPortal:IngestUrl", "https://x.example/ingest/orders"));
        Assert.Contains("SharedSecret", Assert.Throws<InvalidOperationException>(
            () => ClientPortalOptions.FromConfiguration(missingSecret, Path.GetTempPath())).Message);

        var plainHttp = Configuration(("ClientPortal:Enabled", "true"), ("ClientPortal:IngestUrl", "http://x.example/i"), ("ClientPortal:SharedSecret", "s"));
        Assert.Contains("https", Assert.Throws<InvalidOperationException>(
            () => ClientPortalOptions.FromConfiguration(plainHttp, Path.GetTempPath())).Message);

        // The customer mapping is no longer configuration: a leftover ClientPortal:Customers array
        // is ignored rather than validated, because the list now lives in client_portal_customers.
        var leftoverCustomers = Configuration(
            ("ClientPortal:Enabled", "true"), ("ClientPortal:IngestUrl", "https://x.example/i"), ("ClientPortal:SharedSecret", "s"),
            ("ClientPortal:Customers:0:Customer", "Acme"), ("ClientPortal:Customers:0:CustomerId", "Not A Slug"));
        Assert.True(ClientPortalOptions.FromConfiguration(leftoverCustomers, Path.GetTempPath()).Enabled);

        var secretFile = Path.Combine(Path.GetTempPath(), $"portal-secret-{Guid.NewGuid():N}.txt");
        File.WriteAllText(secretFile, "  from-file \r\n");
        try
        {
            var fromFile = ClientPortalOptions.FromConfiguration(Configuration(
                ("ClientPortal:Enabled", "true"), ("ClientPortal:IngestUrl", "https://x.example/i"),
                ("ClientPortal:SharedSecretFile", secretFile)), Path.GetTempPath());
            Assert.Equal("from-file", fromFile.ResolvedSharedSecret);
        }
        finally
        {
            File.Delete(secretFile);
        }
    }

    private static async Task SeedAsync(SqliteDatabase database)
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO cases (id, part_number, name, working_folder_path, customer) VALUES
                ('case-acme', '1036U586-002', 'RIGHT SLOPED LOCKING PLATE', 'C:\Cases\A', 'Acme Fabrication Ltd'),
                ('case-other', 'P-999', 'Unrelated part', 'C:\Cases\B', 'Not Acme Fabrication Ltd At All');
            INSERT INTO orders (id, case_id, order_reference, quantity, work_finish_date, status) VALUES
                ('o-1', 'case-acme', 'E000374633/34980', 6, '2022-12-08', 'in_production'),
                ('o-2', 'case-acme', 'E000384250/36494', 12, '2023-02-19', 'active'),
                ('o-3', 'case-other', 'WO-OTHER', 1, '2026-01-01', 'active');
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static ClientPortalPushService Build(SqliteDatabase database, StubHandler handler, ClientPortalOptions options) =>
        new(options, new SqliteClientPortalCustomerRepository(database),
            new SqliteCaseRepository(database), new SqliteOrderRepository(database),
            new HttpClient(handler), NullLogger<ClientPortalPushService>.Instance);

    /// <summary>Maps the customers that are pushed; this is database state now, not configuration.</summary>
    internal static async Task MapAsync(SqliteDatabase database, params (string Customer, string CustomerId)[] customers)
    {
        var service = new ClientPortalCustomerService(
            new SqliteClientPortalCustomerRepository(database), TimeProvider.System);
        foreach (var customer in customers)
        {
            await service.CreateAsync(customer.CustomerId, customer.Customer);
        }
    }

    private static ClientPortalOptions Options() => ClientPortalOptions.FromConfiguration(
        Configuration(
            ("ClientPortal:Enabled", "true"),
            ("ClientPortal:IngestUrl", "https://ingest.example/ingest/orders"),
            ("ClientPortal:SharedSecret", "s3cret")),
        Path.GetTempPath());

    private static IConfiguration Configuration(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder().AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value))).Build();

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private sealed record RecordedRequest(string Url, string? Authorization, string Body);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), body));
            return respond(request);
        }
    }
}
