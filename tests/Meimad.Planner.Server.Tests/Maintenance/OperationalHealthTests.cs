using System.Net;
using System.Text;
using System.Text.Json;
using Meimad.Planner.Server.Application.EventLogging;
using Meimad.Planner.Server.Application.Maintenance;
using Meimad.Planner.Server.Backup;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Meimad.Planner.Server.Tests.ProductionPackages.ProductionPackageContextTests.Fixture;

namespace Meimad.Planner.Server.Tests.Maintenance;

public sealed class OperationalHealthTests
{
    [Fact]
    public async Task Health_requires_signin_allows_viewers_and_does_not_mutate_planning()
    {
        await using var fixture = await Fixture.CreateAsync();
        using (fixture.Client.SignedOut())
        {
            using var denied = await fixture.Client.GetAsync("/api/v1/operational-health?tabletId=test");
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }
        using (fixture.Client.SignedInWithOnly())
        {
            var revision = await fixture.ScalarAsync("SELECT revision FROM projection_revision");
            using var response = await fixture.Client.GetAsync("/api/v1/operational-health?limit=2");
            response.EnsureSuccessStatusCode();
            Assert.True(response.Headers.CacheControl!.NoStore);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(2, json.RootElement.GetProperty("items").GetArrayLength());
            var cursor = json.RootElement.GetProperty("nextCursor").GetString();
            using var next = await fixture.Client.GetAsync("/api/v1/operational-health?limit=200&cursor=" + Uri.EscapeDataString(cursor!));
            next.EnsureSuccessStatusCode();
            using var nextJson = JsonDocument.Parse(await next.Content.ReadAsStringAsync());
            Assert.DoesNotContain(nextJson.RootElement.GetProperty("items").EnumerateArray(), item => item.GetProperty("category").GetString() is "AI" or "ARTIFACTS");
            Assert.Equal(revision, await fixture.ScalarAsync("SELECT revision FROM projection_revision"));
            using var invalid = await fixture.Client.GetAsync("/api/v1/operational-health?limit=201");
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
    }

    [Fact]
    public async Task Import_failure_retains_last_success_and_does_not_invent_cnc_or_tablet_freshness()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.ExecuteAsync("""
            UPDATE kitaron_sync_state SET sync_status='succeeded',last_completed_at='2026-10-01T10:00:00Z';
            UPDATE kitaron_sync_state SET sync_status='running',last_started_at='2026-10-02T10:00:00Z',last_completed_at=NULL;
            UPDATE kitaron_sync_state SET sync_status='failed',last_completed_at='2026-10-02T10:01:00Z';
            """);
        var report = await fixture.App.Services.GetRequiredService<OperationalHealthService>().ReadAsync(null, 200, default);
        var import = Assert.Single(report.Items, item => item.Category == "ERP_IMPORT");
        Assert.Equal("FAILED", import.Status);
        Assert.Equal(DateTimeOffset.Parse("2026-10-01T10:00:00Z"), import.LastSucceededAt);
        var cnc = Assert.Single(report.Items, item => item.EntityId == "machine-package");
        Assert.Equal("NOT_CONFIGURED", cnc.Status);
        Assert.Equal("UNKNOWN", cnc.Freshness);
        Assert.Null(cnc.ReceivedAt);
    }

    [Fact]
    public async Task Evidence_pages_preserve_same_timestamp_rows_redact_content_and_bind_cursor_to_query()
    {
        await using var fixture = await Fixture.CreateAsync();
        var repository = fixture.App.Services.GetRequiredService<IStructuredEventLogRepository>();
        var at = DateTimeOffset.Parse("2026-10-01T10:00:00Z");
        for (var i = 0; i < 5; i++)
            await repository.AppendAsync(new("test_health", at, "private-user", new Dictionary<string, string>
            { ["machineId"] = "machine-package", ["password"] = "private-secret" }, Comment: "private-comment",
                BeforeData: new { password = "private-secret" }), default);
        var service = fixture.App.Services.GetRequiredService<OperationalEvidenceReader>();
        var first = await service.ReadAsync(at.AddHours(-1), at.AddHours(1), "machineId", "machine-package", null, 2, default);
        var second = await service.ReadAsync(at.AddHours(-1), at.AddHours(1), "machineId", "machine-package", first.NextCursor, 2, default);
        var third = await service.ReadAsync(at.AddHours(-1), at.AddHours(1), "machineId", "machine-package", second.NextCursor, 2, default);
        Assert.Null(third.NextCursor);
        Assert.Equal(5, first.Items.Concat(second.Items).Concat(third.Items).Select(x => x.EventId).Distinct().Count());
        Assert.DoesNotContain("private-", JsonSerializer.Serialize(first));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ReadAsync(at.AddHours(-1), at.AddHours(1), "machineId", "another", first.NextCursor, 2, default));
        var bad = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        { At = (string?)null, Id = "x", From = at.AddHours(-1).ToString("O"), To = at.AddHours(1).ToString("O"), EntityType = "machineId", EntityId = "machine-package" })));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ReadAsync(at.AddHours(-1), at.AddHours(1), "machineId", "machine-package", bad, 2, default));
        using var invalid = await fixture.Client.GetAsync("/api/v1/operational-health/evidence?from=2026-10-01T00:00:00&to=2026-10-02T00:00:00Z");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Theory]
    [InlineData(null, "OVERDUE")]
    [InlineData("In progress", "RUNNING")]
    [InlineData("Failed or interrupted; no new complete set was recorded.", "FAILED_OR_INTERRUPTED")]
    [InlineData("Previous attempt was interrupted; inspect completion markers before retrying.", "INTERRUPTED")]
    public void Backup_attempt_state_is_separate_from_recovery_age(string? message, string expected)
    {
        Assert.Equal(expected, OperationalHealthService.RecoveryState(new(true, null, "external", null, null, message)));
    }

    [Fact]
    public void Freshness_reports_unknown_stale_and_clock_skew_without_changing_connection_state()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal("UNKNOWN", OperationalHealthService.Freshness(now, null, TimeSpan.FromMinutes(1)));
        Assert.Equal("STALE", OperationalHealthService.Freshness(now, now.AddMinutes(-2), TimeSpan.FromMinutes(1)));
        Assert.Equal("CLOCK_SKEW", OperationalHealthService.Freshness(now, now.AddMinutes(1), TimeSpan.FromMinutes(1)));
    }
}
