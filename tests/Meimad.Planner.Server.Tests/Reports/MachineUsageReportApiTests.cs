using System.Net;
using System.Text.Json;
using Meimad.Planner.Server.Application.Reports;
using Meimad.Planner.Server.Application.Timeline;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Meimad.Planner.Server.Tests.Reports;

/// <summary>
/// Machine usage report: each Machine's time as the calculated Timeline shows it, split into
/// production, setup, QC, part reload, reserved, hold, downtime and idle inside its working calendar.
/// </summary>
public sealed class MachineUsageReportApiTests
{
    private const string Report = "/api/v1/reports/machine-usage";

    [Fact]
    public async Task Report_counts_the_timeline_bars_inside_the_machine_calendar()
    {
        await RunAsync(async (application, client) =>
        {
            await SeedAsync(application.Services);
            using var timeline = await client.GetAsync("/api/v1/timeline?from=2026-08-10T00:00:00Z&to=2026-08-12T00:00:00Z");
            var timelineBody = await timeline.Content.ReadAsStringAsync();
            Assert.True(timeline.StatusCode == HttpStatusCode.OK, timelineBody);

            using var response = await client.GetAsync($"{Report}?from=2026-08-11&to=2026-08-11");
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.OK, body);
            using var document = JsonDocument.Parse(body);
            var machine = Assert.Single(document.RootElement.GetProperty("machines").EnumerateArray());
            Assert.Equal("M-1", machine.GetProperty("number").GetString());

            // 08:00-18:00 working time. The Timeline places OP10 setup 08:00-08:30 and its two
            // 30-minute parts 08:30-09:30, OP20's two 15-minute parts 09:30-10:00, and the
            // downtime 10:00-10:30; the rest of the day is idle.
            var metrics = machine.GetProperty("metrics");
            AssertMetrics(metrics, timelineBody,
                available: 36000, production: 5400, setup: 1800, qc: 0, partReload: 0, hold: 0, downtime: 1800, idle: 27000, outside: 0);
            Assert.Equal(20.0m, metrics.GetProperty("usagePercent").GetDecimal());
            Assert.Equal(7200, metrics.GetProperty("usedSeconds").GetInt64());
            AssertMetrics(document.RootElement.GetProperty("totals"), timelineBody,
                available: 36000, production: 5400, setup: 1800, qc: 0, partReload: 0, hold: 0, downtime: 1800, idle: 27000, outside: 0);
            var day = Assert.Single(document.RootElement.GetProperty("days").EnumerateArray());
            Assert.Equal("2026-08-11", day.GetProperty("date").GetString());

            // The day before has no working time: the recorded actual work the Timeline shows there
            // (09:00-12:00) is outside the schedule, and over the whole day it is production.
            using var history = await client.GetAsync($"{Report}?from=2026-08-10&to=2026-08-10");
            using var historyDocument = JsonDocument.Parse(await history.Content.ReadAsStringAsync());
            AssertMetrics(historyDocument.RootElement.GetProperty("totals"), timelineBody,
                available: 0, production: 0, setup: 0, qc: 0, partReload: 0, hold: 0, downtime: 0, idle: 0, outside: 10800);
            using var fullDay = await client.GetAsync($"{Report}?from=2026-08-10&to=2026-08-10&basis=fullDay");
            using var fullDayDocument = JsonDocument.Parse(await fullDay.Content.ReadAsStringAsync());
            AssertMetrics(fullDayDocument.RootElement.GetProperty("totals"), timelineBody,
                available: 86400, production: 10800, setup: 0, qc: 0, partReload: 0, hold: 0, downtime: 0, idle: 75600, outside: 0);
        });
    }

    [Fact]
    public async Task Report_rejects_invalid_periods()
    {
        await RunAsync(async (_, client) =>
        {
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
    public void Each_bar_kind_counts_once_inside_the_period()
    {
        static TimelineProjectionPhase Phase(string type, string start, string end) => new(type, At(start), At(end), null);
        static TimelineProjectionInterval Interval(string type, string start, string end, string? timingKind = null,
            IReadOnlyList<TimelineProjectionPhase>? phases = null) =>
            new(type, "m-1", null, null, null, null, null, null, At(start), At(end), null, timingKind, Phases: phases);
        var machine = new TimelineProjectionMachine("m-1", "M-1", "Mill",
        [
            // Actual history has no phases: all of it is production (07:00 is before the period).
            Interval("actual_history", "07:00", "09:00", "actual"),
            // A forecast block: setup, a part reload, production and QC; the gap 11:30-12:00 is not used.
            Interval("operation", "09:00", "13:00", "forecast",
            [
                Phase("setup", "09:00", "10:00"), Phase("loadunload", "10:00", "10:15"),
                Phase("production", "10:15", "11:30"), Phase("qa", "12:00", "12:30"), Phase("production", "12:30", "13:00")
            ]),
            // A paused block: its waiting phase is hold.
            Interval("operation", "14:00", "16:00", "hold", [Phase("waiting", "14:00", "16:00")]),
            // Downtime under the setup counts as setup, the rest as downtime.
            Interval("downtime", "09:30", "10:00"),
            Interval("downtime", "16:00", "17:00"),
            // Blocked and waiting time is empty space on the Timeline.
            Interval("waiting", "17:00", "18:00", "blocked")
        ]);

        var kinds = MachineUsageReportService.Classify(machine, [(At("08:00"), At("18:00"))]);

        long Seconds(MachineTimeKind kind) => Spans.Seconds(kinds[kind]);
        Assert.Equal(3600 + 4500 + 1800, Seconds(MachineTimeKind.Production));
        Assert.Equal(3600, Seconds(MachineTimeKind.Setup));
        Assert.Equal(900, Seconds(MachineTimeKind.PartReload));
        Assert.Equal(1800, Seconds(MachineTimeKind.Qc));
        Assert.Equal(7200, Seconds(MachineTimeKind.Hold));
        Assert.Equal(3600, Seconds(MachineTimeKind.Downtime));
        Assert.Equal(0, Seconds(MachineTimeKind.Reserved));
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
        JsonElement metrics, string timeline, long available, long production, long setup, long qc, long partReload,
        long hold, long downtime, long idle, long outside)
    {
        Assert.True(
            (available, production, setup, qc, partReload, hold, downtime, idle, outside) ==
            (metrics.GetProperty("availableSeconds").GetInt64(), metrics.GetProperty("productionSeconds").GetInt64(),
             metrics.GetProperty("setupSeconds").GetInt64(), metrics.GetProperty("qcSeconds").GetInt64(),
             metrics.GetProperty("partReloadSeconds").GetInt64(), metrics.GetProperty("holdSeconds").GetInt64(),
             metrics.GetProperty("downtimeSeconds").GetInt64(), metrics.GetProperty("idleSeconds").GetInt64(),
             metrics.GetProperty("outsideScheduleSeconds").GetInt64()),
            $"Report {metrics} for Timeline {timeline}");
    }

    private static DateTimeOffset At(string time) => DateTimeOffset.Parse($"2026-08-11T{time}:00Z");

    private static async Task SeedAsync(IServiceProvider services)
    {
        var database = services.GetRequiredService<SqliteDatabase>();
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO working_calendars (id, name, time_zone_id, calendar_json)
            VALUES ('calendar-1', 'Day shift', 'UTC',
                    '{"availability":[{"startsAt":"2026-08-11T08:00:00Z","endsAt":"2026-08-11T18:00:00Z"}]}');
            INSERT INTO application_settings (key, value)
            VALUES ('timeline.setup_calendar_json',
                    '{"availability":[{"startsAt":"2026-08-11T08:00:00Z","endsAt":"2026-08-11T18:00:00Z"}]}');
            INSERT INTO machines (id, number, name, machine_type, working_calendar_id, status, is_active)
            VALUES ('machine-1', 'M-1', 'Mill One', 'mill', 'calendar-1', 'active', 1);
            INSERT INTO employee_resources (id, employee_number, name, resource_type, first_name, last_name,
                skills_json, assigned_calendar_id, is_active)
            VALUES ('resource-setup', 'E-SETUP', 'Setup Worker', 'setup_worker', 'Setup', 'Worker', '["machine-1"]', 'calendar-1', 1),
                   ('resource-qa', 'E-QA', 'QA Worker', 'qa_worker', 'QA', 'Worker', '[]', 'calendar-1', 1);
            INSERT INTO cases (id, part_number, name, working_folder_path) VALUES ('case-1', 'PN-1', 'Usage Part', 'C:\Cases\PN-1');
            INSERT INTO orders (id, case_id, order_reference, quantity, work_finish_date, status)
            VALUES ('order-1', 'case-1', 'SO-10', 2, '2026-08-12', 'active');
            INSERT INTO production_batches (id, case_id, batch_number, status, planned_quantity)
            VALUES ('batch-1', 'case-1', 'B-1', 'waiting', 2), ('batch-old', 'case-1', 'B-0', 'complete', 2);
            INSERT INTO batch_allocations (id, production_batch_id, allocation_type, order_id, quantity)
            VALUES ('allocation-1', 'batch-1', 'order', 'order-1', 2);
            INSERT INTO case_operations (id, case_id, operation_number, route_position, name, required_machine_type,
                setup_seconds, cycle_seconds, dependency_type, predecessor_case_operation_id)
            VALUES ('case-op-1', 'case-1', 10, 0, 'First', 'mill', 1800, 1800, 'independent', NULL),
                   ('case-op-2', 'case-1', 20, 1, 'Second', 'mill', 0, 900, 'sequential', 'case-op-1');
            INSERT INTO batch_operations (id, production_batch_id, source_case_operation_id, operation_number, route_position,
                name, required_machine_type, setup_seconds, cycle_seconds, status, dependency_type, predecessor_source_case_operation_id)
            VALUES ('op-1', 'batch-1', 'case-op-1', 10, 0, 'First', 'mill', 1800, 1800, 'not_started', 'independent', NULL),
                   ('op-2', 'batch-1', 'case-op-2', 20, 1, 'Second', 'mill', 0, 900, 'not_started', 'sequential', 'case-op-1');
            INSERT INTO batch_operations (id, production_batch_id, source_case_operation_id, operation_number, route_position,
                name, required_machine_type, setup_seconds, cycle_seconds, status, dependency_type,
                actual_start, actual_end, actual_machine_id)
            VALUES ('op-old', 'batch-old', 'case-op-1', 10, 0, 'First', 'mill', 1800, 1800, 'completed', 'independent',
                    '2026-08-10T09:00:00.0000000+00:00', '2026-08-10T12:00:00.0000000+00:00', 'machine-1');
            INSERT INTO machine_assignments (id, batch_operation_id, machine_id, backlog_position)
            VALUES ('assignment-1', 'op-1', 'machine-1', 0), ('assignment-2', 'op-2', 'machine-1', 1);
            INSERT INTO downtimes (id, machine_id, starts_at, ends_at, reason, status)
            VALUES ('downtime-1', 'machine-1', '2026-08-11T10:00:00.0000000+00:00', '2026-08-11T10:30:00.0000000+00:00', 'Inspection', 'planned');
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
                    services.AddSingleton<TimeProvider>(new FixedTimeProvider(DateTimeOffset.Parse("2026-08-11T08:00:00Z")));
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

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
