using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Meimad.Planner.Server.Application.ClientPortal;
using Meimad.Planner.Server.Persistence;
using Meimad.Planner.Server.Tests.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Tests.ClientPortal;

public sealed class ClientPortalCustomerApiTests
{
    [Fact]
    public async Task Customers_can_be_listed_added_renamed_and_removed_without_edit_mode()
    {
        await RunWithServerAsync(async client =>
        {
            // Deliberately no X-Meimad-Client-Id / edit-generation headers: this resource is
            // admin configuration and is not gated by Edit Mode.
            using var empty = await client.GetAsync("/api/v1/client-portal/customers");
            Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
            using var emptyJson = JsonDocument.Parse(await empty.Content.ReadAsStringAsync());
            Assert.Empty(emptyJson.RootElement.GetProperty("items").EnumerateArray());

            using var created = await client.PostAsJsonAsync(
                "/api/v1/client-portal/customers",
                new { customer = "  Acme Fabrication Ltd ", customerId = "acme" });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            Assert.Equal("acme", createdJson.RootElement.GetProperty("customerId").GetString());
            Assert.Equal("Acme Fabrication Ltd", createdJson.RootElement.GetProperty("customer").GetString());

            using var renamed = await client.PutAsJsonAsync(
                "/api/v1/client-portal/customers/acme",
                new { customer = "Acme Fabrication Israel Ltd" });
            Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
            using var renamedJson = JsonDocument.Parse(await renamed.Content.ReadAsStringAsync());
            Assert.Equal("Acme Fabrication Israel Ltd", renamedJson.RootElement.GetProperty("customer").GetString());

            using var listed = await client.GetAsync("/api/v1/client-portal/customers");
            using var listedJson = JsonDocument.Parse(await listed.Content.ReadAsStringAsync());
            var item = Assert.Single(listedJson.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal("acme", item.GetProperty("customerId").GetString());

            using var deleted = await client.DeleteAsync("/api/v1/client-portal/customers/acme");
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

            using var missing = await client.DeleteAsync("/api/v1/client-portal/customers/acme");
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        });
    }

    [Fact]
    public async Task Invalid_portal_ids_and_duplicate_mappings_are_rejected()
    {
        await RunWithServerAsync(async client =>
        {
            using var badId = await client.PostAsJsonAsync(
                "/api/v1/client-portal/customers",
                new { customer = "Acme", customerId = "Not A Slug" });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, badId.StatusCode);
            Assert.Equal("validation_failed", await ErrorCodeAsync(badId));

            using var noName = await client.PostAsJsonAsync(
                "/api/v1/client-portal/customers",
                new { customer = "   ", customerId = "acme" });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, noName.StatusCode);

            using var first = await client.PostAsJsonAsync(
                "/api/v1/client-portal/customers",
                new { customer = "Acme Fabrication Ltd", customerId = "acme" });
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);

            using var sameId = await client.PostAsJsonAsync(
                "/api/v1/client-portal/customers",
                new { customer = "Another Customer", customerId = "acme" });
            Assert.Equal(HttpStatusCode.Conflict, sameId.StatusCode);
            Assert.Equal("client_portal_customer_id_conflict", await ErrorCodeAsync(sameId));

            // The same Customer must never be pushed under two portal ids, case included.
            using var sameName = await client.PostAsJsonAsync(
                "/api/v1/client-portal/customers",
                new { customer = "acme fabrication ltd", customerId = "acme-2" });
            Assert.Equal(HttpStatusCode.Conflict, sameName.StatusCode);
            Assert.Equal("client_portal_customer_name_conflict", await ErrorCodeAsync(sameName));

            using var renameMissing = await client.PutAsJsonAsync(
                "/api/v1/client-portal/customers/nobody",
                new { customer = "Nobody Co" });
            Assert.Equal(HttpStatusCode.NotFound, renameMissing.StatusCode);
        });
    }

    [Fact]
    public async Task Repository_round_trips_rows_ordered_by_customer_name()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        var repository = new SqliteClientPortalCustomerRepository(fixture.Database);
        var service = new ClientPortalCustomerService(repository, TimeProvider.System);

        await service.CreateAsync("zeta", "Zeta Works");
        await service.CreateAsync("acme", "Acme Fabrication Ltd");

        var all = await service.ListAsync();
        Assert.Equal(["Acme Fabrication Ltd", "Zeta Works"], all.Select(value => value.Customer));
        Assert.Equal(["acme", "zeta"], all.Select(value => value.CustomerId));

        var single = await service.GetAsync("acme");
        Assert.NotNull(single);
        Assert.Equal("Acme Fabrication Ltd", single.Customer);
        Assert.True(single.CreatedAt <= single.UpdatedAt);

        await Assert.ThrowsAsync<ClientPortalCustomerNameConflictException>(
            () => service.RenameAsync("zeta", "acme fabrication ltd"));
        await Assert.ThrowsAsync<ClientPortalCustomerNotFoundException>(
            () => service.RenameAsync("missing", "Anything"));
        await Assert.ThrowsAsync<ClientPortalCustomerValidationException>(
            () => service.CreateAsync("UPPER", "Upper Case Id"));

        Assert.True(await service.DeleteAsync("zeta"));
        Assert.False(await service.DeleteAsync("zeta"));
        Assert.Single(await service.ListAsync());
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("error").GetProperty("code").GetString();
    }

    private static async Task RunWithServerAsync(Func<HttpClient, Task> test)
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "MeimadPlanner.ClientPortal.Tests", Guid.NewGuid().ToString("N"));
        var application = ServerApplication.Build(
            ["--Server:Host=127.0.0.1", "--Server:Port=5099", $"--Database:Path={Path.Combine(directory, "test.db")}"],
            builder => builder.UseTestServer());
        try
        {
            await application.StartAsync();
            using var client = application.GetTestClient();
            await test(client);
            await application.StopAsync();
        }
        finally
        {
            await application.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
