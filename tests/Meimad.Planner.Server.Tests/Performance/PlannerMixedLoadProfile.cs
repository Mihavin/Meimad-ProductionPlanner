using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Meimad.Planner.Server.Application.EventLogging;
using Meimad.Planner.Server.Application.LocalAi;
using Meimad.Planner.Server.Application.ProductionPackages;
using Meimad.Planner.Server.Backup;
using Meimad.Planner.Server.Configuration;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Fixture = Meimad.Planner.Server.Tests.ProductionPackages.ProductionPackageContextTests.Fixture;
namespace Meimad.Planner.Server.Tests.Performance;

public sealed class PlannerMixedLoadProfile
{
    [MixedLoadFact]
    public async Task Measure_bounded_readers_evidence_writes_package_builds_complete_backup_and_disabled_AI()
    {
        var operations = int.Parse(Environment.GetEnvironmentVariable("MEIMAD_MIXED_PROFILE_OPERATIONS") ?? "200", System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(operations, 16, 5000); Assert.Equal(0, operations % 4);
        await using var fixture = await Fixture.CreateAsync("--Logging:LogLevel:Default=Warning");
        await PlannerPerformanceProfile.SeedAsync(fixture, operations, "shop", 1000);
        var services = fixture.App.Services;
        var root = Path.GetDirectoryName(fixture.Database.DatabasePath)!;
        const string password = "Disposable-mixed-profile-only-12345";
        var passwordFile = Path.Combine(root, "mixed-profile.dpapi");
        await File.WriteAllBytesAsync(passwordFile, RecoveryKeys.Protect(Encoding.UTF8.GetBytes(password)));
        var backup = new RecoverySetService(fixture.Database, services.GetRequiredService<SqliteBackupService>(),
            new RecoveryOptions { Folder = Path.Combine(root, "mixed-recovery"), PasswordFile = passwordFile, IndependentDestinationConfirmed = true },
            services.GetRequiredService<GCodeOptions>(), services.GetRequiredService<ProductionPackageOptions>(), services.GetRequiredService<EInkOptions>(),
            services.GetRequiredService<IConfiguration>(), services.GetRequiredService<IKeyManager>(), services.GetRequiredService<IDataProtectionProvider>(),
            TimeProvider.System, NullLogger<RecoverySetService>.Instance);
        var observations = new ConcurrentBag<(string Kind, double Ms, string Result)>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5)); var token = timeout.Token;
        var started = Stopwatch.GetTimestamp(); var allocated = GC.GetTotalAllocatedBytes(); var initialMemory = Environment.WorkingSet;
        // Keep elapsed evaluation after the awaited operation (not argument-evaluation order).
        async Task Timed(string kind, Func<Task<string>> action)
        {
            var at = Stopwatch.GetTimestamp(); string result;
            try { result = await action(); } catch (Exception exception) { result = exception.GetType().Name; }
            observations.Add((kind, Stopwatch.GetElapsedTime(at).TotalMilliseconds, result));
        }
        var events = services.GetRequiredService<IStructuredEventLogRepository>();
        var ingestion = Task.Run(async () =>
        {
            for (var i = 0; i < 100; i++)
            {
                await Timed("structured_evidence_append", async () =>
                {
                    await events.AppendAsync(new("mixed-profile-evidence", DateTimeOffset.UtcNow, "profile",
                        new Dictionary<string, string> { ["machineId"] = "machine-package" }, EventKey: "mixed-" + i), token);
                    return "stored";
                });
                await Task.Delay(100, token);
            }
        }, token);
        var readers = Enumerable.Range(0, 3).Select(reader => Task.Run(async () =>
        {
            for (var sample = 0; sample < 8; sample++)
                foreach (var route in new[] { "/api/v1/planning-board", "/api/v1/preparation-queues/SETUP_PENDING" })
                    await Timed(route, async () =>
                    {
                        using var response = await fixture.Client.GetAsync(route, token);
                        _ = await response.Content.ReadAsByteArrayAsync(token);
                        return ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    });
        }, token)).ToArray();
        var packages = services.GetRequiredService<ProductionPackageService>();
        var packageWork = Task.Run(async () =>
        {
            for (var i = 0; i < 2; i++) await Timed("package_build", async () =>
            { await packages.CreateAsync(PlannerShopWorkload.OperationId(i * 4), "profile", cancellationToken: token, requestId: "mixed-package-" + i); return "created"; });
        }, token);
        var recoveryWork = Task.Run(() => Timed("complete_backup_and_restore", async () =>
        {
            var receipt = await backup.CreateAsync("profile", token);
            await RecoverySetService.RestoreAsync(Path.Combine(root, "mixed-recovery", receipt.FileName), password, Path.Combine(root, "mixed-restored"), token);
            return "restored_verified";
        }), token);
        var ai = services.GetRequiredService<LocalAiRequestService>();
        var aiWork = Task.Run(async () =>
        {
            foreach (var (kind, input) in new[]
            {
                ("disabled_ai_summary", new ExplanationRequest("SummarizeShift", From: DateTimeOffset.UtcNow.AddHours(-1), To: DateTimeOffset.UtcNow)),
                ("disabled_ai_blockers", new ExplanationRequest("ExplainBlockers", "run:batch-operation:operation-package"))
            })
                await Timed(kind, async () =>
                {
                    var request = await ai.SubmitAsync("profile", input, token);
                    LocalAiRequestStatus state;
                    do { await Task.Delay(100, token); state = (await ai.ReadAsync("profile", request.RequestId, token))!; }
                    while (state.Status is "QUEUED" or "RUNNING");
                    return state.Result is not { } result ? state.Status + ":" + state.ResultCode
                        : result.Mode + "|" + result.Currency + "|forecast:" +
                            (result.Evidence.Facts.FirstOrDefault(x => x.Kind == "MissingEvidence" && x.Code.StartsWith("timeline.", StringComparison.Ordinal))?.Code
                             ?? (input.Intent == "ExplainBlockers" ? "included" : "not_requested"));
                });
        }, token);
        await Task.WhenAll(readers.Append(ingestion).Append(packageWork).Append(recoveryWork).Append(aiWork));
        var report = new
        {
            measuredAt = DateTimeOffset.UtcNow, operations, activeReaders = 3, archivedEvents = 1000,
            elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds, allocatedBytes = GC.GetTotalAllocatedBytes() - allocated,
            initialWorkingSetBytes = initialMemory, finalWorkingSetBytes = Environment.WorkingSet,
            scope = "Synthetic event-repository ingestion, local complete backup/restore, package builds and disabled AI. No actual CNC, ERP, NAS, TV/tablet or model inference.",
            groups = observations.GroupBy(x => x.Kind).Select(group => new { kind = group.Key, count = group.Count(),
                p50Ms = Percentile(group.Select(x => x.Ms), .50), p95Ms = Percentile(group.Select(x => x.Ms), .95), p99Ms = Percentile(group.Select(x => x.Ms), .99),
                results = group.GroupBy(x => x.Result).ToDictionary(x => x.Key, x => x.Count()) }).ToArray()
        };
        var output = Path.GetFullPath(Environment.GetEnvironmentVariable("MEIMAD_MIXED_PROFILE_OUTPUT")!);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), token);
        Assert.Equal(100L, await fixture.ScalarAsync("SELECT COUNT(*) FROM structured_event_log WHERE event_type='mixed-profile-evidence'"));
        Assert.Contains(observations, x => x.Kind == "complete_backup_and_restore" && x.Result == "restored_verified");
        Assert.Equal(0L, await fixture.ScalarAsync("SELECT COUNT(*) FROM pragma_foreign_key_check"));
    }
    private static double Percentile(IEnumerable<double> values, double percentile)
    { var sorted = values.Order().ToArray(); return sorted[(int)Math.Ceiling(sorted.Length * percentile) - 1]; }
}
internal sealed class MixedLoadFactAttribute : FactAttribute
{
    public MixedLoadFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MEIMAD_MIXED_PROFILE_OUTPUT")))
            Skip = "Opt-in disposable mixed-load measurement; set MEIMAD_MIXED_PROFILE_OUTPUT.";
    }
}
