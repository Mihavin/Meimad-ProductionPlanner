using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Configuration;
using Meimad.Planner.Server.Persistence;
using Meimad.Planner.Server.Tests.Persistence;

namespace Meimad.Planner.Server.Tests.EditMode;

public sealed class SingleEditModeTests
{
    private static readonly DateTimeOffset StartTime =
        new(2026, 8, 11, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Concurrent_clients_produce_one_editor_and_one_pending_request()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        var clock = new ManualTimeProvider(StartTime);
        var service = CreateService(fixture.Database, clock, timeoutSeconds: 30);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var attempts = Enumerable.Range(1, 12).Select(async number =>
        {
            await start.Task;
            try
            {
                var snapshot = await service.RequestEditAsync(
                    $"client-{number}",
                    $"user-{number}");
                return new RequestAttempt(snapshot, null);
            }
            catch (EditModeCommandException exception)
            {
                return new RequestAttempt(null, exception);
            }
        }).ToArray();

        start.SetResult();
        var results = await Task.WhenAll(attempts);

        Assert.Single(results, result => result.Snapshot?.CallerState == EditClientState.Editor);
        Assert.Single(results, result => result.Snapshot?.CallerState == EditClientState.RequestingEdit);
        Assert.Equal(10, results.Count(result => result.Error?.Code == "edit_request_pending"));

        await using var connection = await fixture.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM edit_tokens WHERE id = 1 AND holder_client_id IS NOT NULL),
                (SELECT COUNT(*) FROM edit_requests WHERE status = 'pending');
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal(1, reader.GetInt32(1));
    }

    [Fact]
    public async Task Release_decision_transfers_generation_and_invalidates_old_editor()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        var service = CreateService(fixture.Database, new ManualTimeProvider(StartTime), 30);
        var editor = await service.RequestEditAsync("editor-client", "editor-user");
        var requesting = await service.RequestEditAsync("requester-client", "requester-user");

        var result = await service.DecideAsync(
            requesting.PendingRequest!.RequestId,
            new EditAuthority("editor-client", editor.Generation),
            EditDecision.Release);

        Assert.Equal(EditClientState.Viewer, result.CallerState);
        Assert.Equal("requester-client", result.Holder!.ClientId);
        Assert.Equal(editor.Generation + 1, result.Generation);
        var requesterStatus = await service.GetStatusAsync("requester-client");
        Assert.Equal(EditClientState.Editor, requesterStatus.CallerState);

        var stale = await Assert.ThrowsAsync<EditModeMutationException>(() =>
            service.ReleaseAsync(new EditAuthority("editor-client", editor.Generation)));
        Assert.Equal("edit_generation_stale", stale.Code);
    }

    [Fact]
    public async Task Reject_keeps_editor_and_returns_requester_to_viewer()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        var service = CreateService(fixture.Database, new ManualTimeProvider(StartTime), 30);
        var editor = await service.RequestEditAsync("editor-client", "editor-user");
        var requesting = await service.RequestEditAsync("requester-client", "requester-user");

        var result = await service.DecideAsync(
            requesting.PendingRequest!.RequestId,
            new EditAuthority("editor-client", editor.Generation),
            EditDecision.Reject);

        Assert.Equal(EditClientState.Editor, result.CallerState);
        Assert.Equal("editor-client", result.Holder!.ClientId);
        Assert.Equal(editor.Generation, result.Generation);
        Assert.Null(result.PendingRequest);
        Assert.Equal(
            EditClientState.Viewer,
            (await service.GetStatusAsync("requester-client")).CallerState);
    }

    [Fact]
    public async Task Configured_timeout_auto_transfers_on_server_observation()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        var clock = new ManualTimeProvider(StartTime);
        var service = CreateService(fixture.Database, clock, timeoutSeconds: 7);
        var editor = await service.RequestEditAsync("editor-client", "editor-user");
        var requesting = await service.RequestEditAsync("requester-client", "requester-user");
        var requestId = requesting.PendingRequest!.RequestId;

        clock.Advance(TimeSpan.FromSeconds(7));
        var requesterStatus = await service.GetStatusAsync("requester-client");

        Assert.Equal(EditClientState.Editor, requesterStatus.CallerState);
        Assert.Equal("requester-client", requesterStatus.Holder!.ClientId);
        Assert.Equal(editor.Generation + 1, requesterStatus.Generation);
        var outcome = await service.GetRequestAsync(requestId, "requester-client");
        Assert.Equal(EditRequestStatus.AutoTransferred, outcome!.Status);
        Assert.Equal(requesterStatus.Generation, outcome.GrantedGeneration);
        Assert.Equal(
            EditClientState.Viewer,
            (await service.GetStatusAsync("editor-client")).CallerState);
    }

    [Fact]
    public async Task Voluntary_release_transfers_pending_request_or_clears_token()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        var service = CreateService(fixture.Database, new ManualTimeProvider(StartTime), 30);
        var first = await service.RequestEditAsync("first", "user-1");
        await service.RequestEditAsync("second", "user-2");

        var transferred = await service.ReleaseAsync(new EditAuthority("first", first.Generation));
        Assert.Equal("second", transferred.Holder!.ClientId);
        var secondGeneration = transferred.Generation;

        var released = await service.ReleaseAsync(new EditAuthority("second", secondGeneration));
        Assert.Null(released.Holder);
        Assert.Equal(EditClientState.Viewer, released.CallerState);
        Assert.Equal(secondGeneration + 1, released.Generation);
    }

    [Fact]
    public async Task Concurrent_release_and_reject_leave_one_consistent_editor()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        var service = CreateService(fixture.Database, new ManualTimeProvider(StartTime), 30);
        var editor = await service.RequestEditAsync("editor", "user-1");
        var requesting = await service.RequestEditAsync("requester", "user-2");
        var requestId = requesting.PendingRequest!.RequestId;
        var authority = new EditAuthority("editor", editor.Generation);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<Exception?> Decide(EditDecision decision)
        {
            await start.Task;
            try
            {
                await service.DecideAsync(requestId, authority, decision);
                return null;
            }
            catch (Exception exception) when (
                exception is EditModeCommandException or EditModeMutationException)
            {
                return exception;
            }
        }

        var release = Decide(EditDecision.Release);
        var reject = Decide(EditDecision.Reject);
        start.SetResult();
        await Task.WhenAll(release, reject);

        var editorState = await service.GetStatusAsync("editor");
        var requesterState = await service.GetStatusAsync("requester");
        Assert.Equal(1, new[] { editorState, requesterState }.Count(
            state => state.CallerState == EditClientState.Editor));
        Assert.Equal(editorState.Generation, requesterState.Generation);
        Assert.Null(editorState.PendingRequest);
    }

    private static EditModeService CreateService(
        SqliteDatabase database,
        TimeProvider clock,
        int timeoutSeconds) => new(
        new SqliteEditModeRepository(database),
        new EditModeOptions { TransferTimeoutSeconds = timeoutSeconds },
        clock);

    private sealed record RequestAttempt(
        EditModeSnapshot? Snapshot,
        EditModeCommandException? Error);

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset utcNow;

        internal ManualTimeProvider(DateTimeOffset utcNow)
        {
            this.utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => utcNow;

        internal void Advance(TimeSpan duration)
        {
            utcNow = utcNow.Add(duration);
        }
    }
}
