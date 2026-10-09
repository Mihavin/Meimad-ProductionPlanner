using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text.Json;
using Meimad.Planner.Server.Application.ProductionPackages;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Meimad.Planner.Server.Tests.ProductionPackages.ProductionPackageContextTests.Fixture;

namespace Meimad.Planner.Server.Tests.Performance;

// Explicitly opt-in: disposable Server-owned databases, never an installed factory database.
public sealed class PlannerPerformanceProfile
{
    [PlannerProfileFact]
    public async Task Measure_disposable_planning_workload()
    {
        var output = Environment.GetEnvironmentVariable("MEIMAD_PROFILE_OUTPUT")!;
        var sizes = (Environment.GetEnvironmentVariable("MEIMAD_PROFILE_SIZES") ?? "200,1000,5000")
            .Split(',').Select(int.Parse).ToArray();
        var samples = int.Parse(Environment.GetEnvironmentVariable("MEIMAD_PROFILE_SAMPLES") ?? "5", CultureInfo.InvariantCulture);
        Assert.InRange(samples, 2, 100);
        var workload = Environment.GetEnvironmentVariable("MEIMAD_PROFILE_WORKLOAD") ?? "independent";
        var historyEvents = int.Parse(Environment.GetEnvironmentVariable("MEIMAD_PROFILE_HISTORY") ?? "0", CultureInfo.InvariantCulture);
        Assert.Contains(workload, new[] { "independent", "shop" });
        Assert.InRange(historyEvents, 0, 100000);
        var results = new List<object>();
        var phases = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meter) => { if (instrument.Meter.Name == "Meimad.Planner.Performance") meter.EnableMeasurementEvents(instrument); };
        listener.SetMeasurementEventCallback<double>((instrument, value, _, _) =>
        {
            lock (phases)
            {
                if (!phases.TryGetValue(instrument.Name, out var values)) phases[instrument.Name] = values = [];
                values.Add(value);
            }
        });
        listener.Start();
        foreach (var size in sizes)
        {
            Assert.InRange(size, 16, 5000);
            await using var fixture = await Fixture.CreateAsync("--Logging:LogLevel:Default=Warning");
            await SeedAsync(fixture, size, workload, historyEvents);
            var packages = fixture.App.Services.GetRequiredService<ProductionPackageService>();
            var packageCount = Math.Min(20, workload == "shop" ? size / 4 : size);
            for (var i = 0; i < packageCount; i++)
                await packages.CreateAsync(PlannerShopWorkload.OperationId(workload == "shop" ? i * 4 : i), "profile");
            foreach (var route in new[] { "/api/v1/preparation-queues/SETUP_PENDING", "/api/v1/planning-board",
                "/api/v1/timeline?from=2026-10-08T00:00:00Z&to=2026-10-15T00:00:00Z&asOf=2026-10-08T00:00:00Z",
                "/api/v1/timeline?from=2026-10-08T00:00:00Z&to=2026-11-07T00:00:00Z&asOf=2026-10-08T00:00:00Z",
                "/api/v1/timeline?from=2026-10-08T00:00:00Z&to=2027-04-06T00:00:00Z&asOf=2026-10-08T00:00:00Z",
                "/api/v1/timeline?from=2026-10-08T00:00:00Z&to=2026-11-07T00:00:00Z" }
                .Where(route => route.Contains(Environment.GetEnvironmentVariable("MEIMAD_PROFILE_ROUTE") ?? "", StringComparison.Ordinal)))
            {
                var coldStart = Stopwatch.GetTimestamp();
                double coldMs;
                object? projectionShape = null;
                using (var warmup = await fixture.Client.GetAsync(route))
                {
                    warmup.EnsureSuccessStatusCode();
                    coldMs = Stopwatch.GetElapsedTime(coldStart).TotalMilliseconds;
                    if (route.StartsWith("/api/v1/timeline?", StringComparison.Ordinal))
                    {
                        using var payload = JsonDocument.Parse(await warmup.Content.ReadAsStringAsync());
                        var root = payload.RootElement;
                        projectionShape = new
                        {
                            scheduledOperations = root.GetProperty("machines").EnumerateArray()
                                .SelectMany(machine => machine.GetProperty("intervals").EnumerateArray())
                                .Select(interval => interval.GetProperty("operationId"))
                                .Where(id => id.ValueKind == JsonValueKind.String).Select(id => id.GetString()).Distinct().Count(),
                            dependencies = root.GetProperty("dependencies").GetArrayLength(),
                            resourceIntervals = root.GetProperty("resources").EnumerateArray()
                                .GroupBy(lane => lane.GetProperty("resourceClass").GetString()!)
                                .ToDictionary(group => group.Key, group => group.Sum(lane => lane.GetProperty("intervals").GetArrayLength())),
                            conflicts = root.GetProperty("conflicts").EnumerateArray()
                                .GroupBy(conflict => conflict.GetProperty("code").GetString()!)
                                .ToDictionary(group => group.Key, group => group.Count())
                        };
                    }
                }
                foreach (var clients in new[] { 1, 3, 5 })
                {
                    var elapsed = new List<double>();
                    var bytes = new List<long>();
                    var allocated = GC.GetTotalAllocatedBytes();
                    lock (phases) phases.Clear();
                    for (var sample = 0; sample < samples; sample++)
                    {
                        var observations = await Task.WhenAll(Enumerable.Range(0, clients).Select(async _ =>
                        {
                            var started = Stopwatch.GetTimestamp();
                            using var response = await fixture.Client.GetAsync(route);
                            response.EnsureSuccessStatusCode();
                            var payload = await response.Content.ReadAsByteArrayAsync();
                            return (Milliseconds: Stopwatch.GetElapsedTime(started).TotalMilliseconds, Bytes: payload.LongLength);
                        }));
                        elapsed.AddRange(observations.Select(x => x.Milliseconds));
                        bytes.AddRange(observations.Select(x => x.Bytes));
                    }
                    elapsed.Sort();
                    double Percentile(double p) => elapsed[Math.Clamp((int)Math.Ceiling(p * elapsed.Count) - 1, 0, elapsed.Count - 1)];
                    Dictionary<string, object> timings;
                    lock (phases) timings = phases.ToDictionary(x => x.Key, x =>
                    {
                        var ordered = x.Value.Order().ToArray();
                        double At(double p) => ordered[Math.Clamp((int)Math.Ceiling(p * ordered.Length) - 1, 0, ordered.Length - 1)];
                        return (object)new { count = ordered.Length, totalMs = ordered.Sum(), p50 = At(.5), p95 = At(.95), p99 = At(.99) };
                    });
                    results.Add(new { operations = size, machines = 16, packages = packageCount, workload, historyEvents, route, clients,
                        samples = elapsed.Count, p50Ms = Percentile(.5), p95Ms = Percentile(.95), p99Ms = Percentile(.99),
                        meanBytes = bytes.Average(), allocatedBytes = GC.GetTotalAllocatedBytes() - allocated,
                        workingSetBytes = Process.GetCurrentProcess().WorkingSet64, coldMs, timings, projectionShape });
                    await WriteAsync(); // Keep completed dimensions if a later run is interrupted.
                }
            }
        }
        async Task WriteAsync()
        {
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { recordedAt = DateTimeOffset.UtcNow,
                runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                os = System.Runtime.InteropServices.RuntimeInformation.OSDescription, processors = Environment.ProcessorCount,
                configuration = "Release; in-process HTTP TestServer; synthetic workload; 16 manually assigned Machines; Jerusalem weekday calendar; no external ERP/CNC/device traffic; no WPF rendering",
                workload, historyEvents,
                workloadDescription = workload == "shop"
                    ? "Four-operation batches; all dependency modes; four qualified setup workers, two operators, one QA worker; two capacity-one benches and an external lead-time step; one synthetic closure; retained QC workflow history on one completed Run with a separate recipe and inactive Machine. One managed root recipe plus three unmanaged legacy route steps; no recipe diversity, CNC cycle history or concurrent writes."
                    : "Independent legacy single-output operations sharing one recipe; no auxiliary resource requirements; optional retained QC workflow history on one completed Run/separate recipe/inactive Machine; no concurrent writes.",
                results }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    internal static async Task SeedAsync(Fixture fixture, int count, string workload = "independent", int historyEvents = 0)
    {
        if (workload is not ("independent" or "shop") || count < 16 || count > 5000
            || workload == "shop" && count % 4 != 0 || historyEvents < 0 || historyEvents > 100000)
            throw new ArgumentOutOfRangeException(nameof(count), "Invalid disposable workload dimensions.");
        await using var sql = await fixture.Database.OpenConnectionAsync();
        await using var transaction = sql.BeginTransaction();
        await using var command = sql.CreateCommand();
        command.Transaction = transaction;
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Jerusalem");
        var windows = Enumerable.Range(0, 180).Select(i => new DateTime(2026, 10, 8).AddDays(i))
            .Where(d => d.DayOfWeek is not (DayOfWeek.Friday or DayOfWeek.Saturday)
                && (workload != "shop" || d.Date != new DateTime(2026, 10, 15)))
            .Select(d => new { startsAt = TimeZoneInfo.ConvertTimeToUtc(d.AddHours(6), zone), endsAt = TimeZoneInfo.ConvertTimeToUtc(d.AddHours(18), zone) });
        command.CommandText = "UPDATE working_calendars SET time_zone_id='Asia/Jerusalem',calendar_json=$calendar WHERE id='calendar-package';";
        command.Parameters.AddWithValue("$calendar", JsonSerializer.Serialize(new { availability = windows }));
        await command.ExecuteNonQueryAsync();
        command.Parameters.Clear();
        for (var machine = 1; machine < 16; machine++)
        {
            command.CommandText = """
                INSERT INTO machines(id,number,name,machine_type,working_calendar_id,status,is_active,display_enabled,execution_mode,usable_tool_positions)
                VALUES($id,$id,$id,'mill','calendar-package','active',1,1,'CNC_GCODE',20);
                INSERT INTO machine_supported_postprocessors(machine_id,postprocessor_id,created_at,updated_at)
                VALUES($id,'post-1','2026-10-08','2026-10-08');
                """;
            command.Parameters.AddWithValue("$id", $"profile-machine-{machine}");
            await command.ExecuteNonQueryAsync(); command.Parameters.Clear();
        }
        if (workload == "shop") await PlannerShopWorkload.SeedAsync(command, count);
        else for (var operation = 1; operation < count; operation++)
        {
            command.CommandText = """
                INSERT INTO production_batches(id,case_id,batch_number,status,planned_quantity)
                VALUES($batch,'case-package',$batch,'waiting',10);
                INSERT INTO batch_operations(id,production_batch_id,source_case_operation_id,operation_number,route_position,name,
                    required_machine_type,setup_seconds,cycle_seconds,status)
                VALUES($operation,$batch,'case-operation-package',10,0,'Profile operation','mill',60,60,'not_started');
                INSERT INTO machine_assignments(id,batch_operation_id,machine_id,backlog_position)
                VALUES($assignment,$operation,$machine,$position);
                UPDATE machine_assignments SET selected_gcode_release_id='gcode-1' WHERE id=$assignment;
                """;
            command.Parameters.AddWithValue("$batch", $"profile-batch-{operation}");
            command.Parameters.AddWithValue("$operation", $"profile-op-{operation}");
            command.Parameters.AddWithValue("$assignment", $"profile-assignment-{operation}");
            command.Parameters.AddWithValue("$machine", operation % 16 == 0 ? "machine-package" : $"profile-machine-{operation % 16}");
            command.Parameters.AddWithValue("$position", operation / 16);
            await command.ExecuteNonQueryAsync(); command.Parameters.Clear();
        }
        await PlannerShopWorkload.SeedHistoryAsync(command, historyEvents);
        await transaction.CommitAsync();
    }
}

internal sealed class PlannerProfileFactAttribute : FactAttribute
{
    public PlannerProfileFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MEIMAD_PROFILE_OUTPUT")))
            Skip = "Opt-in disposable benchmark: set MEIMAD_PROFILE_OUTPUT to an output JSON file.";
    }
}


