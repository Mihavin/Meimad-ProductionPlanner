using System.Net;
using System.Text.Json;
using Meimad.Planner.Server.Application.Reports;
using Meimad.Planner.Server.Application.Timeline;
using Meimad.Planner.Server.Configuration;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Meimad.Planner.Server.Tests.Reports;

/// <summary>
/// Machine usage report: recorded time of each Machine in its working calendar, split into
/// production, setup, downtime, no CNC data and idle, per Machine, per day and for the factory.
/// </summary>
public sealed class MachineUsageReportApiTests
{
    private const string Report = "/api/v1/reports/machine-usage";

    [Fact]
    public async Task Report_splits_scheduled_time_into_production_setup_downtime_no_data_and_idle()
    {
        await RunAsync(async (application, client) =>
        {
            await SeedAsync(application.Services);
            using var response = await client.GetAsync($"{Report}?from=2026-08-11&to=2026-08-11");
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.OK, body);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            Assert.Equal("schedule", root.GetProperty("basis").GetString());
            var machines = root.GetProperty("machines").EnumerateArray()
                .ToDictionary(value => value.GetProperty("number").GetString()!, value => value);

            // 08:00-18:00 working time. CNC: running 07-09 and 10-12, offline 12-13, setup 08:30-09:30,
            // downtime 14-15. Running 07-08 is before the shift; running during setup is setup.
            var cnc = machines["M-1"];
            Assert.Equal("cnc", cnc.GetProperty("dataSource").GetString());
            AssertMetrics(cnc.GetProperty("metrics"),
                available: 36000, production: 9000, setup: 3600, downtime: 3600, noData: 3600, idle: 16200, outside: 3600);
            Assert.Equal(35.0m, cnc.GetProperty("metrics").GetProperty("usagePercent").GetDecimal());

            // Manual: setup reported 08-09 (to Send to QC), production session 10-14, a session opened
            // at 14:00 whose run moved to another Machine at 15:00, and a setup started at 16:00 still
            // in progress at midnight (16-18 inside, 18-24 outside the shift).
            var manual = machines["M-2"];
            Assert.Equal("manual", manual.GetProperty("dataSource").GetString());
            AssertMetrics(manual.GetProperty("metrics"),
                available: 36000, production: 18000, setup: 10800, downtime: 0, noData: 0, idle: 7200, outside: 21600);
            Assert.Equal(80.0m, manual.GetProperty("metrics").GetProperty("usagePercent").GetDecimal());

            var totals = root.GetProperty("totals");
            AssertMetrics(totals,
                available: 72000, production: 27000, setup: 14400, downtime: 3600, noData: 3600, idle: 23400, outside: 25200);
            Assert.Equal(57.5m, totals.GetProperty("usagePercent").GetDecimal());
            var day = Assert.Single(root.GetProperty("days").EnumerateArray());
            Assert.Equal("2026-08-11", day.GetProperty("date").GetString());
            Assert.Equal(41400, day.GetProperty("metrics").GetProperty("usedSeconds").GetInt64());

            // Over whole days the manual Machine has 24 h, and nothing is outside it.
            using var fullDay = await client.GetAsync($"{Report}?from=2026-08-11&to=2026-08-11&basis=fullDay");
            using var fullDocument = JsonDocument.Parse(await fullDay.Content.ReadAsStringAsync());
            var fullManual = fullDocument.RootElement.GetProperty("machines").EnumerateArray()
                .Single(value => value.GetProperty("number").GetString() == "M-2");
            AssertMetrics(fullManual.GetProperty("metrics"),
                available: 86400, production: 18000, setup: 32400, downtime: 0, noData: 0, idle: 36000, outside: 0);
        });
    }

    [Fact]
    public async Task Report_counts_only_time_until_now_and_rejects_invalid_periods()
    {
        await RunAsync(async (application, client) =>
        {
            await SeedAsync(application.Services);
            using var future = await client.GetAsync($"{Report}?from=2026-08-12&to=2026-08-13");
            using var futureDocument = JsonDocument.Parse(await future.Content.ReadAsStringAsync());
            Assert.Equal(0, futureDocument.RootElement.GetProperty("totals").GetProperty("availableSeconds").GetInt64());
            Assert.Equal(JsonValueKind.Null, futureDocument.RootElement.GetProperty("totals").GetProperty("usagePercent").ValueKind);
            Assert.Equal(2, futureDocument.RootElement.GetProperty("days").GetArrayLength());

            foreach (var query in new[]
                     {
                         "from=2026-08-12&to=2026-08-11", "from=2026-05-01&to=2026-08-11",
                         "from=2026-08-11&to=2026-08-11&basis=week", "from=11.08.2026"
                     })
            {
                using var invalid = await client.GetAsync($"{Report}?{query}");
                Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
            }
        });
    }

    [Fact]
    public async Task A_cnc_state_holds_until_the_last_poll_and_later_time_has_no_data()
    {
        var repository = new FakeRepository(new MachineUsageSource(
            [new MachineUsageSourceMachine(Machine("m-1", "M-1"), true, At("12:00"))],
            null, null, [], [],
            [new MachineStateChange("m-1", At("08:00"), "ONLINE", "ACTIVE")],
            [], []));
        var service = new MachineUsageReportService(repository, new TimelineOptions { TimeZoneId = "UTC" },
            new FixedTimeProvider(DateTimeOffset.Parse("2026-08-12T00:00:00Z")));

        var report = await service.CalculateAsync(new DateOnly(2026, 8, 11), new DateOnly(2026, 8, 11), null);

        var metrics = Assert.Single(report.Machines).Metrics;
        Assert.Equal((36000L, 14400L, 21600L, 0L), (metrics.AvailableSeconds, metrics.ProductionSeconds, metrics.NoDataSeconds, metrics.IdleSeconds));
        Assert.Equal(40.0m, metrics.UsagePercent);
    }

    [Fact]
    public void Spans_union_intersect_and_subtract_sorted_sets()
    {
        static (DateTimeOffset, DateTimeOffset) S(string start, string end) => (At(start), At(end));
        var left = Spans.Normalize([S("10:00", "12:00"), S("08:00", "09:00"), S("08:30", "09:30")]);
        Assert.Equal([S("08:00", "09:30"), S("10:00", "12:00")], left);
        var right = Spans.Normalize([S("09:00", "10:30"), S("11:00", "11:15"), S("11:45", "13:00")]);
        Assert.Equal([S("09:00", "09:30"), S("10:00", "10:30"), S("11:00", "11:15"), S("11:45", "12:00")], Spans.Intersect(left, right));
        Assert.Equal([S("08:00", "09:00"), S("10:30", "11:00"), S("11:15", "11:45")], Spans.Subtract(left, right));
        Assert.Equal(5400 + 7200, Spans.Seconds(left));
    }

    private static void AssertMetrics(
        JsonElement metrics, long available, long production, long setup, long downtime, long noData, long idle, long outside)
    {
        Assert.Equal(
            (available, production, setup, downtime, noData, idle, outside),
            (metrics.GetProperty("availableSeconds").GetInt64(), metrics.GetProperty("productionSeconds").GetInt64(),
             metrics.GetProperty("setupSeconds").GetInt64(), metrics.GetProperty("downtimeSeconds").GetInt64(),
             metrics.GetProperty("noDataSeconds").GetInt64(), metrics.GetProperty("idleSeconds").GetInt64(),
             metrics.GetProperty("outsideScheduleSeconds").GetInt64()));
    }

    private static DateTimeOffset At(string time) => DateTimeOffset.Parse($"2026-08-11T{time}:00Z");

    private static TimelineSourceMachine Machine(string id, string number) => new(
        id, number, number, "UTC",
        """{"availability":[{"startsAt":"2026-08-11T08:00:00Z","endsAt":"2026-08-11T18:00:00Z"}]}""", [], false);

    private static async Task SeedAsync(IServiceProvider services)
    {
        var database = services.GetRequiredService<SqliteDatabase>();
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO working_calendars (id, name, time_zone_id, calendar_json)
            VALUES ('calendar-1', 'Day shift', 'UTC',
                    '{"availability":[{"startsAt":"2026-08-11T08:00:00Z","endsAt":"2026-08-11T18:00:00Z"}]}');
            INSERT INTO machines (id, number, name, machine_type, working_calendar_id, status, is_active)
            VALUES ('machine-cnc', 'M-1', 'CNC Mill', 'mill', 'calendar-1', 'active', 1),
                   ('machine-manual', 'M-2', 'Manual Lathe', 'mill', 'calendar-1', 'active', 1);
            INSERT INTO machine_connections (
                id, machine_id, adapter_type, enabled, connection_status, polling_interval_ms,
                connection_timeout_ms, maximum_reconnect_backoff_ms, allow_read, allow_write,
                configuration_json, raw_telemetry_retention_days, version, created_at, updated_at)
            VALUES ('connection-cnc', 'machine-cnc', 'HAAS_NGC', 0, 'DISABLED', 1000,
                    3000, 30000, 1, 0, '{}', 14, 1, '2026-08-01T00:00:00Z', '2026-08-01T00:00:00Z');
            INSERT INTO machine_state_history (id, machine_id, connection_id, observed_at, change_kind, snapshot_json)
            VALUES
                ('h0', 'machine-cnc', 'connection-cnc', '2026-08-10T20:00:00.0000000+00:00', 'MEANINGFUL_CHANGE', '{"connectionStatus":"ONLINE","machineState":{"value":"STOPPED"}}'),
                ('h1', 'machine-cnc', 'connection-cnc', '2026-08-11T07:00:00.0000000+00:00', 'MEANINGFUL_CHANGE', '{"connectionStatus":"ONLINE","machineState":{"value":"ACTIVE"}}'),
                ('h2', 'machine-cnc', 'connection-cnc', '2026-08-11T09:00:00.0000000+00:00', 'MEANINGFUL_CHANGE', '{"connectionStatus":"DEGRADED","machineState":{"value":"STOPPED"}}'),
                ('h3', 'machine-cnc', 'connection-cnc', '2026-08-11T10:00:00.0000000+00:00', 'MEANINGFUL_CHANGE', '{"connectionStatus":"ONLINE","machineState":{"value":"ACTIVE"}}'),
                ('h4', 'machine-cnc', 'connection-cnc', '2026-08-11T12:00:00.0000000+00:00', 'MEANINGFUL_CHANGE', '{"connectionStatus":"OFFLINE","machineState":{"value":"ACTIVE"}}'),
                ('h5', 'machine-cnc', 'connection-cnc', '2026-08-11T13:00:00.0000000+00:00', 'MEANINGFUL_CHANGE', '{"connectionStatus":"ONLINE","machineState":{"value":"READY"}}');
            INSERT INTO downtimes (id, machine_id, starts_at, ends_at, reason, status)
            VALUES ('downtime-1', 'machine-cnc', '2026-08-11T14:00:00.0000000+00:00', '2026-08-11T15:00:00.0000000+00:00', 'Inspection', 'planned');
            INSERT INTO cases (id, part_number, name, working_folder_path) VALUES ('case-1', 'PN-1', 'Usage Part', 'C:\Cases\PN-1');
            INSERT INTO production_batches (id, case_id, batch_number, status, planned_quantity) VALUES ('batch-1', 'case-1', 'B-1', 'waiting', 4);
            INSERT INTO case_operations (id, case_id, operation_number, route_position, name, required_machine_type, setup_seconds, cycle_seconds, dependency_type)
            VALUES ('case-op-1', 'case-1', 10, 0, 'Mill', 'mill', 1800, 600, 'independent');
            INSERT INTO batch_operations (id, production_batch_id, source_case_operation_id, operation_number, route_position, name,
                required_machine_type, setup_seconds, cycle_seconds, status, dependency_type)
            VALUES ('op-1', 'batch-1', 'case-op-1', 10, 0, 'Mill', 'mill', 1800, 600, 'in_progress', 'independent'),
                   ('op-2', 'batch-1', 'case-op-1', 20, 1, 'Mill', 'mill', 1800, 600, 'in_progress', 'independent'),
                   ('op-3', 'batch-1', 'case-op-1', 30, 2, 'Mill', 'mill', 1800, 600, 'in_progress', 'independent'),
                   ('op-4', 'batch-1', 'case-op-1', 40, 3, 'Mill', 'mill', 1800, 600, 'in_progress', 'independent');
            INSERT INTO production_runs (id, status, legacy_batch_operation_id)
            VALUES ('run-cnc', 'PLANNED', 'op-1'), ('run-manual', 'PLANNED', 'op-2'), ('run-open', 'PLANNED', 'op-3'), ('run-moved', 'PLANNED', 'op-4');
            INSERT INTO production_run_workflow_events (id, production_run_id, machine_id, event_type, source, source_event_id, server_received_at, metadata_json)
            VALUES
                ('e1', 'run-cnc', 'machine-cnc', 'OFFSET_LOADER_COMPLETED', 'CNC', 'olc-1', '2026-08-11T08:30:00.0000000+00:00', '{}'),
                ('e2', 'run-cnc', 'machine-cnc', 'SEND_TO_QC', 'TABLET', 'qc-1', '2026-08-11T09:30:00.0000000+00:00', '{}'),
                ('e3', 'run-manual', 'machine-manual', 'MANUAL_SETUP_RUN', 'PLANNER_MANUAL', 'm-1', '2026-08-11T08:00:00.0000000+00:00', '{}'),
                ('e4', 'run-manual', 'machine-manual', 'SEND_TO_QC', 'PLANNER_MANUAL', 'm-2', '2026-08-11T09:00:00.0000000+00:00', '{}'),
                ('e5', 'run-manual', 'machine-manual', 'PRODUCTION_SESSION_OPENED', 'PLANNER_MANUAL', 'm-3', '2026-08-11T10:00:00.0000000+00:00', '{}'),
                ('e6', 'run-manual', 'machine-manual', 'PRODUCTION_SESSION_CLOSED', 'PLANNER_MANUAL', 'm-4', '2026-08-11T14:00:00.0000000+00:00', '{"producedQuantity":4}'),
                ('e7', 'run-open', 'machine-manual', 'MANUAL_SETUP_RUN', 'PLANNER_MANUAL', 'm-5', '2026-08-11T16:00:00.0000000+00:00', '{}'),
                ('e8', 'run-moved', 'machine-manual', 'PRODUCTION_SESSION_OPENED', 'PLANNER_MANUAL', 'm-6', '2026-08-11T14:00:00.0000000+00:00', '{}'),
                ('e9', 'run-moved', 'machine-cnc', 'SEND_TO_QC', 'TABLET', 'qc-2', '2026-08-11T15:00:00.0000000+00:00', '{}');
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task RunAsync(Func<WebApplication, HttpClient, Task> test)
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), "MeimadPlanner.MachineUsage.Tests", Guid.NewGuid().ToString("N"));
        var application = ServerApplication.Build(
            ["--Server:Host=127.0.0.1", "--Server:Port=5099", $"--Database:Path={Path.Combine(directoryPath, "api-test.db")}",
             "--Timeline:TimeZoneId=UTC"],
            webHost =>
            {
                webHost.UseSignedInTestServer();
                webHost.ConfigureServices(services =>
                {
                    services.RemoveAll<TimeProvider>();
                    services.AddSingleton<TimeProvider>(new FixedTimeProvider(DateTimeOffset.Parse("2026-08-12T00:00:00Z")));
                });
            });
        try
        {
            await application.StartAsync();
            using var client = application.GetTestClient();
            await test(application, client);
            await application.StopAsync();
        }
        finally
        {
            await application.DisposeAsync();
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(directoryPath)) Directory.Delete(directoryPath, recursive: true); }
            catch (IOException) { } // A pooled handle may still hold the file; the temp folder is disposable.
        }
    }

    private sealed class FakeRepository(MachineUsageSource source) : IMachineUsageRepository
    {
        public Task<MachineUsageSource> ReadAsync(DateTimeOffset from, DateTimeOffset to, DateTimeOffset now, CancellationToken cancellationToken) =>
            Task.FromResult(source);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
