using Meimad.Planner.Server.Application.MachineEvidence;
using Meimad.Planner.Server.Configuration;
using Meimad.Planner.Server.Domain.Cnc;
using Meimad.Planner.Server.Persistence;
using Meimad.Planner.Server.Tests.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meimad.Planner.Server.Tests.MachineEvidence;

/// <summary>
/// Exporter definition §20.15 acceptance tests 1-3, 6-8 and 10 through the real pipeline: DPRNT lines
/// as the adapters deliver them (<c>DPRINT_EVENT</c> telemetry) → <see cref="WorkOffsetDprntConsumer"/> →
/// <see cref="WorkOffsetIngestService"/> → SQLite (schema v116).
/// </summary>
public sealed class WorkOffsetIngestTests
{
    [Fact]
    public async Task Acceptance_1_a_complete_Haas_G54_frame_is_stored_VALID_and_becomes_current()
    {
        await using var fixture = await CreateAsync();
        await fixture.FeedAsync("machine-10", WorkOffsetTestData.Frame("WO-10-20261012-63000-1"));

        var view = await fixture.ReadAsync("machine-10");
        var current = Assert.Single(view.Current);
        Assert.Equal(WorkOffsetStatuses.Valid, current.Status);
        Assert.Equal("G54", current.WorkOffset);
        Assert.Equal(WorkOffsetTestData.CurrentRelease, current.NcReleaseId);
        Assert.Equal(654321, current.NcIdentityToken);
        Assert.Equal(20, current.OperationNumber);
        Assert.Equal(125.402, current.PositionMm.X!.Value, 6);
        Assert.Equal(-80.016, current.PositionMm.Y!.Value, 6);
        Assert.Equal(42.691, current.PositionMm.Z!.Value, 6);
        Assert.Null(current.RotationDeg.A);
        Assert.Equal("MM", current.Units);
        Assert.Equal(WorkOffsetSources.DprntTcp, current.Source);
        Assert.Equal("WO-10-20261012-63000-1", current.SourceSession);
        Assert.Equal("DPRNT:cnc-10", current.ReceivedBy);
        Assert.Equal(fixture.Time.GetUtcNow(), current.ReceivedAt);
        Assert.Equal(64, current.RawMessageHash.Length);
        Assert.Contains("MEIMAD/OFFSET-END/SESSION/WO-10-20261012-63000-1", current.RawMessage);
        Assert.True(current.NcReleaseIsCurrent);
    }

    [Fact]
    public async Task Acceptance_2_a_transmission_cut_off_before_END_stores_nothing()
    {
        await using var fixture = await CreateAsync();
        var frame = WorkOffsetTestData.Frame("WO-10-20261012-63000-1");
        await fixture.FeedAsync("machine-10", frame.Take(3).ToArray());
        Assert.Equal(0L, await fixture.CountAsync());

        // The next report starts: the cut-off session is dropped for good, never stored later.
        await fixture.FeedAsync("machine-10", WorkOffsetTestData.Frame("WO-10-20261012-63100-1").Take(2).ToArray());
        await fixture.FeedAsync("machine-10", [frame[3]]);
        Assert.Equal(0L, await fixture.CountAsync());
    }

    [Fact]
    public async Task Acceptance_3_a_repeated_session_with_the_same_content_is_ignored()
    {
        await using var fixture = await CreateAsync();
        var frame = WorkOffsetTestData.Frame("WO-10-20261012-63000-1");
        await fixture.FeedAsync("machine-10", frame);
        await fixture.FeedAsync("machine-10", frame);
        // A restarted reader replaying a print file is a fresh parser: still the same evidence.
        await fixture.NewConsumer().ConsumeAsync("machine-10", Telemetry("machine-10", frame), CancellationToken.None);
        // The same message with other padding (FANUC blanks) is the same evidence too.
        await fixture.FeedAsync("machine-10", frame.Select(line => line.Replace("/X/", "/X/  ", StringComparison.Ordinal)).ToArray());

        Assert.Equal(1L, await fixture.CountAsync());
        Assert.Single((await fixture.ReadAsync("machine-10")).Current);
    }

    [Fact]
    public async Task A_reused_session_with_other_content_is_kept_as_REJECTED_and_never_applied()
    {
        await using var fixture = await CreateAsync();
        await fixture.FeedAsync("machine-10", WorkOffsetTestData.Frame("WO-10-20261012-63000-1"));
        await fixture.FeedAsync("machine-10", WorkOffsetTestData.Frame("WO-10-20261012-63000-1", x: 125.5));

        var history = (await fixture.ReadAsync("machine-10")).History;
        Assert.Equal(2, history.Count);
        var reused = history.Single(record => record.Status == WorkOffsetStatuses.Rejected);
        Assert.Equal("session_reused", reused.StatusReason);
        Assert.Equal(125.402, Assert.Single((await fixture.ReadAsync("machine-10")).Current).PositionMm.X!.Value, 6);
    }

    [Fact]
    public async Task Acceptance_6_a_message_for_another_Machine_is_UNMATCHED()
    {
        await using var fixture = await CreateAsync();
        // Machine 14's report arrives on Machine 10's DPRNT channel.
        await fixture.FeedAsync("machine-10", WorkOffsetTestData.Frame("WO-14-20261012-63000-1", machine: "14"));

        var record = Assert.Single(await fixture.Service.ListAsync("UNMATCHED", null, null, null));
        Assert.Equal("wrong_machine", record.StatusReason);
        Assert.Equal("machine-10", record.MachineId);
        Assert.Equal("14", record.Claimed.Machine);
        Assert.Null(record.CaseOperationId);
        Assert.Null(record.NcReleaseId);
        Assert.Empty((await fixture.ReadAsync("machine-10")).Current);
        Assert.Empty((await fixture.ReadAsync("machine-14")).History);
    }

    [Theory]
    [InlineData("999999", "20", "unknown_release")]
    [InlineData("654330", "20", "operation_mismatch")]
    [InlineData("654321", "30", "operation_mismatch")]
    public async Task Release_or_Operation_that_does_not_match_is_UNMATCHED(string release, string operation, string reason)
    {
        await using var fixture = await CreateAsync();
        await fixture.FeedAsync("machine-10", WorkOffsetTestData.Frame("S-1", release: release, operation: operation));
        var record = Assert.Single(await fixture.Service.ListAsync(null, "machine-10", null, null));
        Assert.Equal(WorkOffsetStatuses.Unmatched, record.Status);
        Assert.Equal(reason, record.StatusReason);
    }

    [Fact]
    public async Task Acceptance_7_a_message_for_an_old_NC_release_needs_review()
    {
        await using var fixture = await CreateAsync();
        await fixture.FeedAsync("machine-10", WorkOffsetTestData.Frame("S-old", release: "654320"));

        var record = Assert.Single((await fixture.ReadAsync("machine-10")).History);
        Assert.Equal(WorkOffsetStatuses.ReviewRequired, record.Status);
        Assert.Equal("stale_release", record.StatusReason);
        Assert.Equal(WorkOffsetTestData.OldRelease, record.NcReleaseId);
        Assert.False(record.NcReleaseIsCurrent);
        Assert.Empty((await fixture.ReadAsync("machine-10")).Current);
    }

    [Fact]
    public async Task Acceptance_8_a_large_change_is_kept_as_a_candidate_and_does_not_become_current()
    {
        await using var fixture = await CreateAsync();
        await fixture.FeedAsync("machine-10", WorkOffsetTestData.Frame("S-1"));
        fixture.Time.Advance(TimeSpan.FromMinutes(5));
        await fixture.FeedAsync("machine-10", WorkOffsetTestData.Frame("S-2", x: 130.0));

        var view = await fixture.ReadAsync("machine-10");
        var current = Assert.Single(view.Current);
        Assert.Equal(125.402, current.PositionMm.X!.Value, 6);
        var candidate = view.History.Single(record => record.Status == WorkOffsetStatuses.ReviewRequired);
        Assert.Equal("large_change", candidate.StatusReason);
        Assert.Equal(130.0, candidate.PositionMm.X!.Value, 6);

        // A small re-measurement is accepted and supersedes the previous current record.
        fixture.Time.Advance(TimeSpan.FromMinutes(5));
        await fixture.FeedAsync("machine-10", WorkOffsetTestData.Frame("S-3", x: 125.45));
        view = await fixture.ReadAsync("machine-10");
        var next = Assert.Single(view.Current);
        Assert.Equal(125.45, next.PositionMm.X!.Value, 6);
        Assert.Equal(current.WorkOffsetId, next.SupersedesId);
        Assert.Equal(WorkOffsetStatuses.Superseded, view.History.Single(record => record.WorkOffsetId == current.WorkOffsetId).Status);
        Assert.Equal(WorkOffsetStatuses.Valid, view.History.Single(record => record.WorkOffsetId == current.WorkOffsetId).InitialStatus);
    }

    [Fact]
    public async Task Acceptance_10_the_last_VALID_measurement_is_there_after_a_Server_restart()
    {
        await using var fixture = await CreateAsync();
        await fixture.FeedAsync("machine-10", WorkOffsetTestData.Frame("S-1"));
        fixture.Time.Advance(TimeSpan.FromMinutes(1));
        await fixture.FeedAsync("machine-10", WorkOffsetTestData.Frame("S-2", x: 125.41));
        SqliteConnection.ClearAllPools();

        // A new Server process: new repository and services over the same database file.
        var restarted = new WorkOffsetService(
            new SqliteWorkOffsetRepository(new SqliteDatabase(new DatabaseOptions(fixture.Database.DatabasePath))),
            null, TimeProvider.System);
        var view = await restarted.ReadOperationAsync("machine-10", WorkOffsetTestData.Operation, "G54", null);
        var current = Assert.Single(view!.Current);
        Assert.Equal(125.41, current.PositionMm.X!.Value, 6);
        Assert.Equal(2, view.History.Count);
    }

    [Fact]
    public async Task Invalid_content_of_a_complete_frame_is_kept_as_REJECTED_evidence()
    {
        await using var fixture = await CreateAsync();
        var frame = WorkOffsetTestData.Frame("S-1");
        frame[2] = frame[2].Replace("/Z/42.6910", "/Z/42.6910/Z/42.7", StringComparison.Ordinal);
        await fixture.FeedAsync("machine-10", frame);

        var record = Assert.Single(await fixture.Service.ListAsync("REJECTED", null, null, null));
        Assert.Equal("duplicate_axis", record.StatusReason);
        Assert.Equal("machine-10", record.MachineId);
        Assert.Empty((await fixture.ReadAsync("machine-10")).Current);
    }

    [Theory]
    [InlineData("machine-11", "11", "G154 P1", "unsupported_work_offset")]
    [InlineData("machine-10", "10", "G54.1 P1", "unsupported_work_offset")]
    [InlineData("machine-13", "13", "G54", "unsupported_work_offset")]
    public async Task A_work_offset_the_Machine_s_control_does_not_have_is_REJECTED(
        string machineId, string machine, string offset, string reason)
    {
        await using var fixture = await CreateAsync();
        await fixture.FeedAsync(machineId, WorkOffsetTestData.Frame("S-1", machine: machine, offset: offset.Replace(" ", "", StringComparison.Ordinal)));
        var record = Assert.Single(await fixture.Service.ListAsync(null, machineId, null, null));
        Assert.Equal(WorkOffsetStatuses.Rejected, record.Status);
        Assert.Equal(reason, record.StatusReason);
    }

    [Theory]
    [InlineData("machine-11", "11", "G541P1", "G54.1 P1")]
    [InlineData("machine-12", "12", "G54.1P48", "G54.1 P48")]
    [InlineData("machine-13", "13", "G15H1", "G15 H1")]
    [InlineData("machine-10", "10", "G154P1", "G154 P1")]
    [InlineData("machine-10", "10", "G110", "G154 P1")]
    public async Task Each_control_stores_its_own_work_offset_names(string machineId, string machine, string offset, string stored)
    {
        await using var fixture = await CreateAsync();
        await fixture.FeedAsync(machineId, WorkOffsetTestData.Frame("S-1", machine: machine, offset: offset));
        var record = Assert.Single(await fixture.Service.ListAsync("VALID", machineId, null, null));
        Assert.Equal(stored, record.WorkOffset);
    }

    [Fact]
    public async Task Inch_values_are_stored_in_millimetres_and_values_out_of_range_are_REJECTED()
    {
        await using var fixture = await CreateAsync();
        await fixture.FeedAsync("machine-10", WorkOffsetTestData.Frame("S-1", x: 1.0, y: -2.0, z: 0.5, units: "20"));
        var current = Assert.Single((await fixture.ReadAsync("machine-10")).Current);
        Assert.Equal("INCH", current.Units);
        Assert.Equal(25.4, current.PositionMm.X!.Value, 6);
        Assert.Equal(-50.8, current.PositionMm.Y!.Value, 6);
        Assert.Equal(12.7, current.PositionMm.Z!.Value, 6);

        await fixture.FeedAsync("machine-10", WorkOffsetTestData.Frame("S-2", x: 99999.0));
        Assert.Equal("value_out_of_range",
            Assert.Single(await fixture.Service.ListAsync("REJECTED", null, null, null)).StatusReason);
    }

    [Fact]
    public async Task A_measurement_on_an_inactive_Machine_is_UNMATCHED()
    {
        await using var fixture = await CreateAsync();
        await fixture.FeedAsync("machine-15", WorkOffsetTestData.Frame("S-1", machine: "15"));
        Assert.Equal("machine_inactive", Assert.Single(await fixture.Service.ListAsync("UNMATCHED", null, null, null)).StatusReason);
    }

    [Fact]
    public async Task Each_work_offset_and_each_NC_release_has_its_own_current_measurement()
    {
        await using var fixture = await CreateAsync();
        await fixture.FeedAsync("machine-10", WorkOffsetTestData.Frame("S-1"));
        await fixture.FeedAsync("machine-10", WorkOffsetTestData.Frame("S-2", offset: "G55", x: 300));
        await fixture.FeedAsync("machine-14", WorkOffsetTestData.Frame("S-3", machine: "14", x: 10));

        var view = await fixture.ReadAsync("machine-10", offset: null);
        Assert.Equal(["G54", "G55"], view.Current.Select(record => record.WorkOffset).Order());
        Assert.Single((await fixture.ReadAsync("machine-10")).Current);
        Assert.Equal(10, Assert.Single((await fixture.ReadAsync("machine-14")).Current).PositionMm.X!.Value, 6);
    }

    [Fact]
    public async Task The_measurement_rows_are_append_only()
    {
        await using var fixture = await CreateAsync();
        await fixture.FeedAsync("machine-10", WorkOffsetTestData.Frame("S-1"));
        await using var connection = await fixture.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE machine_work_offsets SET x_mm = 1;";
        await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
        command.CommandText = "DELETE FROM machine_work_offsets;";
        await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
        command.CommandText = "UPDATE machine_work_offsets SET status = 'REJECTED', version = version + 1;";
        await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
        command.CommandText = "UPDATE machine_work_offsets SET status = 'SUPERSEDED';";
        await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(1L, await fixture.CountAsync());
    }

    internal static IReadOnlyList<RawCncTelemetry> Telemetry(string machineId, IEnumerable<string> lines) =>
        lines.Select(line => new RawCncTelemetry(
            machineId, "cnc-" + machineId["machine-".Length..], CncAdapterTypes.HaasNgc,
            WorkOffsetTestData.At, "DPRINT_EVENT", line)).ToArray();

    internal static async Task<Fixture> CreateAsync()
    {
        var database = await TemporaryDatabase.CreateAsync();
        await WorkOffsetTestData.SeedAsync(database.Database);
        await using (var connection = await database.Database.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO machine_connections (
                    id, machine_id, adapter_type, enabled, connection_status, polling_interval_ms,
                    connection_timeout_ms, maximum_reconnect_backoff_ms, allow_read, allow_write,
                    configuration_json, raw_telemetry_retention_days, version, created_at, updated_at)
                VALUES ('cnc-10', 'machine-10', 'HAAS_NGC', 0, 'DISABLED', 1000, 3000, 30000, 1, 0,
                        '{"host":"10.0.0.10","dprnt":{"source":"TCP","enabled":true}}', 14, 1,
                        '2026-10-01T00:00:00Z', '2026-10-01T00:00:00Z');
                """;
            await command.ExecuteNonQueryAsync();
        }
        return new Fixture(database);
    }

    internal sealed class MutableTime(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset now = now;
        public override DateTimeOffset GetUtcNow() => now;
        internal void Advance(TimeSpan by) => now += by;
    }

    internal sealed class Fixture(TemporaryDatabase database) : IAsyncDisposable
    {
        internal SqliteDatabase Database => database.Database;
        internal MutableTime Time { get; } = new(WorkOffsetTestData.At.AddHours(1));
        private readonly SqliteWorkOffsetRepository repository = new(database.Database);
        private WorkOffsetDprntConsumer? consumer;

        internal WorkOffsetService Service => new(repository, null, Time);

        internal WorkOffsetDprntConsumer NewConsumer() => new(
            new WorkOffsetIngestService(repository, new WorkOffsetOptions(), Time, NullLogger<WorkOffsetIngestService>.Instance),
            repository, NullLogger<WorkOffsetDprntConsumer>.Instance);

        internal Task FeedAsync(string machineId, IReadOnlyList<string> lines)
        {
            consumer ??= NewConsumer();
            return consumer.ConsumeAsync(machineId, Telemetry(machineId, lines), CancellationToken.None);
        }

        internal async Task<WorkOffsetOperationView> ReadAsync(string machineId, string? offset = "G54") =>
            (await Service.ReadOperationAsync(machineId, WorkOffsetTestData.Operation, offset, null))!;

        internal async Task<long> CountAsync()
        {
            await using var connection = await Database.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM machine_work_offsets;";
            return (long)(await command.ExecuteScalarAsync())!;
        }

        public ValueTask DisposeAsync() => database.DisposeAsync();
    }
}
