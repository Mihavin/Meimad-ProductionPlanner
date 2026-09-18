using System.Text;
using Meimad.Planner.Server.Infrastructure.Haas;

namespace Meimad.Planner.Server.Tests.Haas;

public sealed class HaasDprntFileReaderTests : IDisposable
{
    private readonly string path = Path.Combine(
        Path.GetTempPath(), "MeimadPlanner.Dprnt", Guid.NewGuid().ToString("N"), "print.txt");

    public HaasDprntFileReaderTests() => Directory.CreateDirectory(Path.GetDirectoryName(path)!);

    [Fact]
    public async Task Returns_only_lines_appended_since_the_previous_poll()
    {
        await File.WriteAllTextAsync(path, "30P647004101-001\r\nMEIMAD/V/1/OLC/AAA\r\n");
        var reader = new HaasDprntFileReader();

        var first = await reader.DrainAsync(path, false, CancellationToken.None);
        var quiet = await reader.DrainAsync(path, false, CancellationToken.None);
        await File.AppendAllTextAsync(path, "16E2509-7PSOFI-1\r\nMEIMAD/V/1/CST/BBB\r\n");
        var second = await reader.DrainAsync(path, false, CancellationToken.None);

        // A fresh reader recovers the current PartName but never replays historical events.
        Assert.Equal("30P647004101-001", first.PartName);
        Assert.Empty(first.EventLines);
        Assert.Null(quiet.PartName);
        Assert.Empty(quiet.EventLines);
        Assert.Equal("16E2509-7PSOFI-1", second.PartName);
        Assert.Equal(new[] { "MEIMAD/V/1/CST/BBB" }, second.EventLines);
        Assert.Equal("30P647004101-001\r\nMEIMAD/V/1/OLC/AAA\r\n16E2509-7PSOFI-1\r\nMEIMAD/V/1/CST/BBB\r\n",
            await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Fresh_reader_replaying_existing_content_returns_everything_the_file_still_holds()
    {
        await File.WriteAllTextAsync(path, "30P647004101-001\r\nMEIMAD/V/1/OLC/AAA\r\nMEIMAD/V/1/CST/BBB\r\n");
        var reader = new HaasDprntFileReader();

        var first = await reader.DrainAsync(path, true, CancellationToken.None);

        Assert.Equal("30P647004101-001", first.PartName);
        Assert.Equal(new[] { "MEIMAD/V/1/OLC/AAA", "MEIMAD/V/1/CST/BBB" }, first.EventLines);
        Assert.True(reader.TryTruncateConsumed(path));
        Assert.Equal(0L, new FileInfo(path).Length);
    }

    [Fact]
    public async Task Fresh_reader_without_replay_starts_from_the_tail_of_a_large_file()
    {
        var builder = new StringBuilder();
        for (var index = 0; index < 8000; index++)
            builder.Append("OLD-PART-").Append(index).Append("\r\nMEIMAD/V/1/OLC/").Append(index).Append("\r\n");
        builder.Append("30P647004101-001\r\n");
        await File.WriteAllTextAsync(path, builder.ToString());
        var reader = new HaasDprntFileReader();

        var first = await reader.DrainAsync(path, false, CancellationToken.None);
        await File.AppendAllTextAsync(path, "MEIMAD/V/1/CST/NEW\r\n");
        var second = await reader.DrainAsync(path, false, CancellationToken.None);

        Assert.Equal("30P647004101-001", first.PartName);
        Assert.Empty(first.EventLines);
        Assert.Null(second.PartName);
        Assert.Equal(new[] { "MEIMAD/V/1/CST/NEW" }, second.EventLines);
    }

    [Fact]
    public async Task Truncation_waits_while_the_controller_is_still_writing_a_line()
    {
        await File.WriteAllTextAsync(path, "30P647004101-001\r\n16E25");
        var reader = new HaasDprntFileReader();

        var partial = await reader.DrainAsync(path, true, CancellationToken.None);
        Assert.False(reader.TryTruncateConsumed(path));
        var untouched = new FileInfo(path).Length;
        await File.AppendAllTextAsync(path, "09-7PSOFI-1\r\n");
        var complete = await reader.DrainAsync(path, true, CancellationToken.None);
        Assert.True(reader.TryTruncateConsumed(path));

        Assert.Equal("30P647004101-001", partial.PartName);
        Assert.Equal(23L, untouched);
        Assert.Equal("16E2509-7PSOFI-1", complete.PartName);
        Assert.Equal(0L, new FileInfo(path).Length);
    }

    [Fact]
    public async Task Truncation_is_skipped_when_the_controller_appended_after_the_read()
    {
        await File.WriteAllTextAsync(path, "30P647004101-001\r\n");
        var reader = new HaasDprntFileReader();

        var first = await reader.DrainAsync(path, true, CancellationToken.None);
        await File.AppendAllTextAsync(path, "16E2509-7PSOFI-1\r\n");
        Assert.False(reader.TryTruncateConsumed(path));
        var second = await reader.DrainAsync(path, true, CancellationToken.None);

        Assert.Equal("30P647004101-001", first.PartName);
        Assert.Equal("16E2509-7PSOFI-1", second.PartName);
        Assert.Null(reader.LastTruncateError);
    }

    [Fact]
    public async Task Holds_an_unterminated_line_until_the_controller_finishes_it()
    {
        await File.WriteAllTextAsync(path, "30P6470");
        var reader = new HaasDprntFileReader();

        var partial = await reader.DrainAsync(path, false, CancellationToken.None);
        await File.AppendAllTextAsync(path, "04101-001\r\n");
        var complete = await reader.DrainAsync(path, false, CancellationToken.None);

        Assert.Null(partial.PartName);
        Assert.Equal("30P647004101-001", complete.PartName);
    }

    [Fact]
    public async Task Rereads_a_file_the_controller_rewrote_from_the_start()
    {
        await File.WriteAllTextAsync(path, "30P647004101-001\r\n");
        var reader = new HaasDprntFileReader();
        await reader.DrainAsync(path, false, CancellationToken.None);

        // Overwrite mode: same length, different content.
        await File.WriteAllTextAsync(path, "30P647004101-002\r\n");
        var rewrittenSameLength = await reader.DrainAsync(path, false, CancellationToken.None);
        // Overwrite mode: shorter content.
        await File.WriteAllTextAsync(path, "AB-1\r\n");
        var rewrittenShorter = await reader.DrainAsync(path, false, CancellationToken.None);
        // Overwrite mode: longer content with a different prefix.
        await File.WriteAllTextAsync(path, "16E2509-7PSOFI-1\r\nMEIMAD/V/1/CST/BBB\r\n");
        var rewrittenLonger = await reader.DrainAsync(path, false, CancellationToken.None);

        Assert.Equal("30P647004101-002", rewrittenSameLength.PartName);
        Assert.Equal("AB-1", rewrittenShorter.PartName);
        Assert.Equal("16E2509-7PSOFI-1", rewrittenLonger.PartName);
        Assert.Equal(new[] { "MEIMAD/V/1/CST/BBB" }, rewrittenLonger.EventLines);
    }

    [Fact]
    public async Task Truncation_empties_the_consumed_file_and_keeps_tailing()
    {
        await File.WriteAllTextAsync(path, "30P647004101-001\r\n");
        var reader = new HaasDprntFileReader();

        var first = await reader.DrainAsync(path, true, CancellationToken.None);
        Assert.True(reader.TryTruncateConsumed(path));
        var emptied = new FileInfo(path).Length;
        var quiet = await reader.DrainAsync(path, true, CancellationToken.None);
        Assert.False(reader.TryTruncateConsumed(path));
        await File.AppendAllTextAsync(path, "16E2509-7PSOFI-1\r\n");
        var second = await reader.DrainAsync(path, true, CancellationToken.None);
        Assert.True(reader.TryTruncateConsumed(path));

        Assert.Equal("30P647004101-001", first.PartName);
        Assert.Equal(0L, emptied);
        Assert.Null(reader.LastTruncateError);
        Assert.Null(quiet.PartName);
        Assert.Equal("16E2509-7PSOFI-1", second.PartName);
        Assert.Equal(0L, new FileInfo(path).Length);
    }

    [Fact]
    public async Task Tolerates_FANUC_DC2_DC4_control_codes_around_DPRNT_output()
    {
        // POPEN emits DC2 before the first line and PCLOS emits DC4 after the last one.
        await File.WriteAllBytesAsync(path, Encoding.ASCII.GetBytes(
            "30P647004101-001\r\nMEIMAD/V/1/EVENT/CST/ID/NC-1-S-1/SEQ/1/MACROVERSION/6/PROGRAM/654321\r\n"));
        var reader = new HaasDprntFileReader();

        var result = await reader.DrainAsync(path, true, CancellationToken.None);

        Assert.Equal("30P647004101-001", result.PartName);
        Assert.Equal(new[] { "MEIMAD/V/1/EVENT/CST/ID/NC-1-S-1/SEQ/1/MACROVERSION/6/PROGRAM/654321" }, result.EventLines);
    }

    [Fact]
    public async Task Ignores_padding_nulls_and_non_part_lines()
    {
        await File.WriteAllBytesAsync(path, Encoding.ASCII.GetBytes("\0\0Part Name: X\r\n1500.CNC\r\nO1500\r\n30P647004101-001\r\n"));
        var reader = new HaasDprntFileReader();

        var result = await reader.DrainAsync(path, false, CancellationToken.None);

        Assert.Equal("30P647004101-001", result.PartName);
        Assert.Empty(result.EventLines);
    }

    [Fact]
    public async Task Empty_or_missing_file_is_reported_without_inventing_a_part()
    {
        await File.WriteAllTextAsync(path, string.Empty);
        var reader = new HaasDprntFileReader();

        var empty = await reader.DrainAsync(path, false, CancellationToken.None);
        Assert.Null(empty.PartName);
        Assert.Empty(empty.EventLines);
        Assert.False(reader.TryTruncateConsumed(path));

        File.Delete(path);
        await Assert.ThrowsAnyAsync<IOException>(() => reader.DrainAsync(path, false, CancellationToken.None));
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch { }
    }
}
