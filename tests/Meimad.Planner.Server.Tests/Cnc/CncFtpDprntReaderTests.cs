using System.Text;
using Meimad.Planner.Server.Infrastructure.Cnc;

namespace Meimad.Planner.Server.Tests.Cnc;

public sealed class CncFtpDprntReaderTests
{
    private static CncFtpDprntEndpoint Endpoint(FakeFtpServer server) =>
        new(new Uri($"ftp://127.0.0.1:{server.Port}/print.txt"), "anonymous", "x");

    [Fact]
    public async Task Only_the_text_appended_since_the_previous_poll_is_returned()
    {
        await using var server = new FakeFtpServer { FileContent = Encoding.UTF8.GetBytes("30P647004101-001\r\n") };
        var reader = new CncFtpDprntReader();

        var first = await reader.DrainAsync(Endpoint(server), replayExistingContent: false, CancellationToken.None);
        Assert.Equal("30P647004101-001", first.PartName);
        Assert.Empty(first.EventLines);

        server.FileContent = Encoding.UTF8.GetBytes(
            "30P647004101-001\r\nMEIMAD/V/1/EVENT/CST/ID/NC-1-S-1/SEQ/2/MACROVERSION/6/PROGRAM/1\r\n");
        var second = await reader.DrainAsync(Endpoint(server), replayExistingContent: false, CancellationToken.None);
        Assert.Equal(new[] { "MEIMAD/V/1/EVENT/CST/ID/NC-1-S-1/SEQ/2/MACROVERSION/6/PROGRAM/1" }, second.EventLines);
    }

    [Fact]
    public async Task The_DPRNT_log_gets_each_new_line_once_also_in_replay_mode()
    {
        await using var server = new FakeFtpServer { FileContent = Encoding.UTF8.GetBytes("logged before the restart\r\n") };
        var reader = new CncFtpDprntReader();
        var first = await reader.DrainAsync(Endpoint(server), replayExistingContent: false, CancellationToken.None);
        server.FileContent = Encoding.UTF8.GetBytes("logged before the restart\r\npingret\r\n\r\nT12 D=10.000\r\n");
        var second = await reader.DrainAsync(Endpoint(server), replayExistingContent: false, CancellationToken.None);
        Assert.Empty(first.AllLines);
        Assert.Equal(new[] { "pingret", "T12 D=10.000" }, second.AllLines);

        // Replay mode reads the whole file every poll for its events, but logs each line once.
        server.FileContent = Encoding.UTF8.GetBytes("a\r\n");
        var replayReader = new CncFtpDprntReader();
        var replayFirst = await replayReader.DrainAsync(Endpoint(server), replayExistingContent: true, CancellationToken.None);
        var replaySame = await replayReader.DrainAsync(Endpoint(server), replayExistingContent: true, CancellationToken.None);
        server.FileContent = Encoding.UTF8.GetBytes("a\r\nb\r\n");
        var replayMore = await replayReader.DrainAsync(Endpoint(server), replayExistingContent: true, CancellationToken.None);
        Assert.Equal(new[] { "a" }, replayFirst.AllLines);
        Assert.Empty(replaySame.AllLines);
        Assert.Equal(new[] { "b" }, replayMore.AllLines);
    }

    [Fact]
    public async Task A_file_larger_than_the_download_window_logs_only_the_text_appended_since_the_previous_poll()
    {
        // The reader keeps the last 4 MB of a large file; the previous tail then starts inside the
        // new one, and only what follows it is new (audit finding 2026-10-08: the whole tail was
        // logged again on every poll).
        var filler = new StringBuilder();
        for (var line = 0; filler.Length < 5 * 1024 * 1024; line++) filler.Append("filler line ").Append(line).Append("\r\n");
        await using var server = new FakeFtpServer { FileContent = Encoding.UTF8.GetBytes(filler.ToString()) };
        var reader = new CncFtpDprntReader();
        var first = await reader.DrainAsync(Endpoint(server), replayExistingContent: false, CancellationToken.None);
        Assert.Empty(first.AllLines);

        server.FileContent = Encoding.UTF8.GetBytes(filler + "30P647004101-001\r\nMEIMAD/V/1/EVENT/CST/ID/NC-1-S-1/SEQ/1/MACROVERSION/6/PROGRAM/1\r\n");
        var second = await reader.DrainAsync(Endpoint(server), replayExistingContent: false, CancellationToken.None);

        Assert.Equal(new[] { "30P647004101-001", "MEIMAD/V/1/EVENT/CST/ID/NC-1-S-1/SEQ/1/MACROVERSION/6/PROGRAM/1" }, second.AllLines);
        Assert.Equal("30P647004101-001", second.PartName);
        Assert.Single(second.EventLines);

        var quiet = await reader.DrainAsync(Endpoint(server), replayExistingContent: false, CancellationToken.None);
        Assert.Empty(quiet.AllLines);
        Assert.Empty(quiet.EventLines);

        // Without the previous tail the file was rewritten: everything is new.
        Assert.Equal("new", CncFtpDprntReader.NewText("old content", "new"));
        Assert.Equal("c", CncFtpDprntReader.NewText("ab", "abc"));
    }

    [Fact]
    public async Task A_rewritten_or_shortened_file_is_read_again_from_the_start()
    {
        await using var server = new FakeFtpServer
        {
            FileContent = Encoding.UTF8.GetBytes("30P647004101-001\r\nsome more text\r\n")
        };
        var reader = new CncFtpDprntReader();
        await reader.DrainAsync(Endpoint(server), replayExistingContent: false, CancellationToken.None);

        server.FileContent = Encoding.UTF8.GetBytes("16E2509-7PSOFI-1\r\n");
        var afterRewrite = await reader.DrainAsync(Endpoint(server), replayExistingContent: false, CancellationToken.None);

        Assert.Equal("16E2509-7PSOFI-1", afterRewrite.PartName);
    }

    [Fact]
    public async Task Replay_mode_returns_the_whole_current_content_every_poll()
    {
        await using var server = new FakeFtpServer
        {
            FileContent = Encoding.UTF8.GetBytes(
                "30P647004101-001\r\nMEIMAD/V/1/EVENT/CST/ID/NC-1-S-1/SEQ/2/MACROVERSION/6/PROGRAM/1\r\n")
        };
        var reader = new CncFtpDprntReader();

        var result = await reader.DrainAsync(Endpoint(server), replayExistingContent: true, CancellationToken.None);

        Assert.Equal("30P647004101-001", result.PartName);
        Assert.Single(result.EventLines);
    }

    [Fact]
    public async Task Clearing_uploads_an_empty_file_only_when_the_content_still_matches_what_was_read()
    {
        await using var server = new FakeFtpServer { FileContent = Encoding.UTF8.GetBytes("30P647004101-001\r\n") };
        var reader = new CncFtpDprntReader();
        await reader.DrainAsync(Endpoint(server), replayExistingContent: false, CancellationToken.None);

        var cleared = await reader.TryTruncateConsumedAsync(Endpoint(server), CancellationToken.None);

        Assert.True(cleared);
        Assert.NotNull(server.LastUpload);
        Assert.Empty(server.LastUpload!);
    }

    [Fact]
    public async Task Clearing_is_skipped_when_the_controller_appended_a_line_after_the_read()
    {
        await using var server = new FakeFtpServer { FileContent = Encoding.UTF8.GetBytes("30P647004101-001\r\n") };
        var reader = new CncFtpDprntReader();
        await reader.DrainAsync(Endpoint(server), replayExistingContent: false, CancellationToken.None);

        // The controller appends a line between the read and the verify-before-clear download.
        server.BeforeDataTransfer = () =>
            server.FileContent = Encoding.UTF8.GetBytes("30P647004101-001\r\nnew line\r\n");

        var cleared = await reader.TryTruncateConsumedAsync(Endpoint(server), CancellationToken.None);

        Assert.False(cleared);
        Assert.Null(server.LastUpload);
    }
}
