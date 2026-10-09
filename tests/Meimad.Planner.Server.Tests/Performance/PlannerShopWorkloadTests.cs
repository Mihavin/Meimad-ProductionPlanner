using System.Text.Json;
using Xunit;
using Fixture = Meimad.Planner.Server.Tests.ProductionPackages.ProductionPackageContextTests.Fixture;

namespace Meimad.Planner.Server.Tests.Performance;

public sealed class PlannerShopWorkloadTests
{
    [Theory]
    [InlineData(16, 0)]
    [InlineData(32, 512)]
    public async Task Shop_workload_projects_every_resource_class_and_dependency_without_reassigning_work(int operations, int history)
    {
        await using var fixture = await Fixture.CreateAsync();
        await PlannerPerformanceProfile.SeedAsync(fixture, operations, "shop", history);
        Assert.Equal((long)operations, await fixture.ScalarAsync("SELECT COUNT(*) FROM batch_operations WHERE status='not_started'"));
        Assert.Equal((long)history, await fixture.ScalarAsync("SELECT COUNT(*) FROM production_run_workflow_events WHERE source='PROFILE'"));
        Assert.Equal(4L, await fixture.ScalarAsync("SELECT COUNT(DISTINCT dependency_type) FROM batch_operations"));
        Assert.Equal(0L, await fixture.ScalarAsync("SELECT COUNT(*) FROM pragma_foreign_key_check"));
        var assignments = await AssignmentSnapshotAsync(fixture);
        await using (var connection = await fixture.Database.OpenConnectionAsync())
        await using (var query = connection.CreateCommand())
        {
            query.CommandText = "SELECT group_concat(id) FROM production_runs WHERE NOT EXISTS (SELECT 1 FROM production_run_programs WHERE production_run_id=production_runs.id)";
            var empty = await query.ExecuteScalarAsync();
            Assert.True(empty is DBNull || empty is null, $"Empty Runs: {empty}");
        }
        using var response = await fixture.Client.GetAsync("/api/v1/timeline?from=2026-10-08T00:00:00Z&to=2026-11-07T00:00:00Z&asOf=2026-10-08T00:00:00Z");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        var scheduled = root.GetProperty("machines").EnumerateArray().SelectMany(machine => machine.GetProperty("intervals").EnumerateArray())
            .Where(interval => interval.GetProperty("operationId").ValueKind == JsonValueKind.String)
            .Select(interval => interval.GetProperty("operationId").GetString()).Distinct().ToArray();
        Assert.Equal(operations, scheduled.Length);
        var classes = root.GetProperty("resources").EnumerateArray()
            .Where(lane => lane.GetProperty("intervals").GetArrayLength() > 0)
            .Select(lane => lane.GetProperty("resourceClass").GetString()).Distinct().ToArray();
        Assert.Contains("workstation", classes);
        Assert.Contains("employee", classes);
        Assert.Contains("external", classes);
        foreach (var lane in root.GetProperty("resources").EnumerateArray()
            .Where(lane => lane.GetProperty("resourceClass").GetString() == "workstation"))
        {
            var slots = lane.GetProperty("intervals").EnumerateArray()
                .OrderBy(slot => slot.GetProperty("startsAt").GetDateTimeOffset()).ToArray();
            for (var i = 1; i < slots.Length; i++)
                Assert.True(slots[i - 1].GetProperty("endsAt").GetDateTimeOffset() <= slots[i].GetProperty("startsAt").GetDateTimeOffset(),
                    "Capacity-one workstations must not overlap.");
        }
        Assert.Equal(assignments, await AssignmentSnapshotAsync(fixture));
        // Adding unrelated retained evidence must not erase or change current projections.
        if (history == 0)
        {
            await using (var connection = await fixture.Database.OpenConnectionAsync())
            await using (var transaction = connection.BeginTransaction())
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                await PlannerShopWorkload.SeedHistoryAsync(command, 100);
                await transaction.CommitAsync();
            }
            using var next = await fixture.Client.GetAsync("/api/v1/timeline?from=2026-10-08T00:00:00Z&to=2026-11-07T00:00:00Z&asOf=2026-10-08T00:00:00Z");
            next.EnsureSuccessStatusCode();
            using var updated = JsonDocument.Parse(await next.Content.ReadAsStringAsync());
            // Historical missing-actual diagnostics can legitimately be added for the
            // archived Run; current scheduling and authoritative assignments must not change.
            foreach (var key in new[] { "machines", "resources", "dependencies" })
                Assert.Equal(root.GetProperty(key).GetRawText(), updated.RootElement.GetProperty(key).GetRawText());
            Assert.Equal(assignments, await AssignmentSnapshotAsync(fixture));
        }
    }

    private static async Task<string> AssignmentSnapshotAsync(Fixture fixture)
    {
        await using var connection = await fixture.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT group_concat(fact,'|') FROM (SELECT id || ':' || machine_id || ':' || backlog_position || ':' || version AS fact FROM machine_assignments ORDER BY id)";
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
