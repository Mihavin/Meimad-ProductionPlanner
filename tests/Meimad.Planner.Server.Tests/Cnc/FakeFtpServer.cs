using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Meimad.Planner.Server.Tests.Cnc;

/// <summary>
/// A minimal embedded FTP server good enough to exercise <c>FtpWebRequest</c>'s RETR/STOR
/// passive-mode flow, standing in for a controller's own FTP server in tests. Single file,
/// single connection at a time; unknown commands are acknowledged and ignored.
/// </summary>
internal sealed class FakeFtpServer : IAsyncDisposable
{
    private readonly TcpListener listener;
    private readonly CancellationTokenSource cts = new();
    private readonly Task acceptLoop;

    internal FakeFtpServer()
    {
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        acceptLoop = Task.Run(() => AcceptLoopAsync(cts.Token));
    }

    internal int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    /// <summary>What RETR returns; mutate between polls to simulate the controller appending, rewriting, or clearing the file.</summary>
    internal byte[] FileContent { get; set; } = [];

    /// <summary>What STOR most recently received.</summary>
    internal byte[]? LastUpload { get; private set; }

    /// <summary>Runs just before RETR/STOR opens the data connection, e.g. to mutate <see cref="FileContent"/> mid-poll.</summary>
    internal Action? BeforeDataTransfer { get; set; }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(token);
                await HandleClientAsync(client, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        await using var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
        await writer.WriteLineAsync("220 fake ftp ready");

        TcpListener? dataListener = null;
        try
        {
            while (true)
            {
                var line = await reader.ReadLineAsync(token);
                if (line is null) return;
                var space = line.IndexOf(' ');
                var command = (space < 0 ? line : line[..space]).ToUpperInvariant();
                var argument = space < 0 ? string.Empty : line[(space + 1)..];

                switch (command)
                {
                    case "USER":
                        await writer.WriteLineAsync("331 password please");
                        break;
                    case "PASS":
                        await writer.WriteLineAsync("230 logged in");
                        break;
                    case "TYPE":
                        await writer.WriteLineAsync("200 type set");
                        break;
                    case "PASV":
                        dataListener?.Stop();
                        dataListener = new TcpListener(IPAddress.Loopback, 0);
                        dataListener.Start();
                        var port = ((IPEndPoint)dataListener.LocalEndpoint).Port;
                        await writer.WriteLineAsync(
                            $"227 Entering Passive Mode (127,0,0,1,{port / 256},{port % 256})");
                        break;
                    case "RETR":
                        BeforeDataTransfer?.Invoke();
                        await writer.WriteLineAsync("150 opening data connection");
                        using (var data = await dataListener!.AcceptTcpClientAsync(token))
                        await using (var dataStream = data.GetStream())
                            await dataStream.WriteAsync(FileContent, token);
                        await writer.WriteLineAsync("226 transfer complete");
                        break;
                    case "STOR":
                        BeforeDataTransfer?.Invoke();
                        await writer.WriteLineAsync("150 opening data connection");
                        using (var data = await dataListener!.AcceptTcpClientAsync(token))
                        await using (var buffer = new MemoryStream())
                        {
                            await using var dataStream = data.GetStream();
                            await dataStream.CopyToAsync(buffer, token);
                            LastUpload = buffer.ToArray();
                        }
                        await writer.WriteLineAsync("226 transfer complete");
                        break;
                    case "QUIT":
                        await writer.WriteLineAsync("221 bye");
                        return;
                    default:
                        await writer.WriteLineAsync("200 ok");
                        break;
                }
            }
        }
        finally
        {
            dataListener?.Stop();
        }
    }

    public async ValueTask DisposeAsync()
    {
        cts.Cancel();
        listener.Stop();
        try { await acceptLoop; } catch { }
        cts.Dispose();
    }
}
