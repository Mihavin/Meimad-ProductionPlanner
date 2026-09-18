using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Meimad.Planner.Server.Application.Fanuc;
using Meimad.Planner.Server.Domain.Cnc;

namespace Meimad.Planner.Server.Infrastructure.Fanuc;

internal sealed class FocasClientFactory : IFocasClientFactory
{
    public IFocasClient Create(FanucFocasConnectionConfiguration configuration) => new FocasClient(configuration);
}

/// <summary>
/// Read-only FOCAS 2 Ethernet session over the native library. Every native call for one
/// handle runs on one dedicated thread, because a FOCAS library handle must not be shared
/// across threads; the async surface only marshals to and from that thread.
/// </summary>
internal sealed class FocasClient : IFocasClient
{
    private readonly FanucFocasConnectionConfiguration configuration;
    private readonly NativeThread thread;
    private ushort? handle;

    internal FocasClient(FanucFocasConnectionConfiguration configuration)
    {
        this.configuration = configuration;
        thread = new NativeThread($"focas-{configuration.Host}");
    }

    public bool Connected => handle is not null;

    public Task ConnectAsync(CancellationToken cancellationToken = default) => thread.RunAsync(() =>
    {
        if (handle is not null) return 0;
        if (!FocasNative.TryEnsureLoaded(out var message))
            throw new FocasLibraryUnavailableException(message);
        FocasNative.EnsureStarted();
        var timeoutSeconds = Math.Max(1, (int)Math.Ceiling(configuration.TimeoutMs / 1000d));
        var result = FocasNative.cnc_allclibhndl3(
            configuration.Host, checked((ushort)configuration.Port), timeoutSeconds, out var allocated);
        if (result == FocasNative.EW_NODLL)
            throw new FocasException(
                $"FOCAS could not open {configuration.Host}:{configuration.Port}: cnc_allclibhndl3 returned EW_NODLL ({result}). "
                + "The Ethernet library fwlibe64.dll and the control-series fwlib*64.dll files from the FANUC FOCAS kit must sit "
                + $"next to {FocasNative.LibraryFileName}.", result);
        if (result != FocasNative.EW_OK)
            throw new FocasException(
                $"FOCAS could not open {configuration.Host}:{configuration.Port} (cnc_allclibhndl3 returned {result}"
                + (result == FocasNative.EW_SOCKET ? ", EW_SOCKET: no answer on the FOCAS port" : string.Empty) + ").", result);
        handle = allocated;
        return 0;
    }, cancellationToken);

    public Task DisconnectAsync(CancellationToken cancellationToken = default) => thread.RunAsync(() =>
    {
        if (handle is { } current)
        {
            handle = null;
            _ = FocasNative.cnc_freelibhndl(current);
        }
        return 0;
    }, cancellationToken);

    public Task<FocasControllerStatus> ReadStatusAsync(CancellationToken cancellationToken = default) =>
        thread.RunAsync(() =>
        {
            var current = RequireHandle();
            Check(FocasNative.cnc_statinfo(current, out var status), "cnc_statinfo");
            string? programNumber = null;
            string? programName = null;
            var nameResult = FocasNative.cnc_exeprgname(current, out var executing);
            if (nameResult == FocasNative.EW_OK)
            {
                programName = Ascii(executing.name);
                if (executing.o_num > 0)
                    programNumber = "O" + executing.o_num.ToString(CultureInfo.InvariantCulture);
                else if (!string.IsNullOrWhiteSpace(programName))
                    programNumber = programName;
            }
            else if (nameResult == FocasNative.EW_FUNC
                     && FocasNative.cnc_rdprgnum(current, out var numbers) == FocasNative.EW_OK
                     && numbers.data > 0)
            {
                programNumber = "O" + numbers.data.ToString(CultureInfo.InvariantCulture);
            }

            decimal? spindle = FocasNative.cnc_acts(current, out var actualSpindle) == FocasNative.EW_OK
                ? actualSpindle.data : null;
            decimal? feed = FocasNative.cnc_actf(current, out var actualFeed) == FocasNative.EW_OK
                ? actualFeed.data : null;
            int? alarmCount = null;
            short alarmSlots = 20;
            var alarmBuffer = new byte[alarmSlots * FocasNative.AlarmMessageSize];
            if (FocasNative.cnc_rdalmmsg2(current, -1, ref alarmSlots, alarmBuffer) == FocasNative.EW_OK)
                alarmCount = alarmSlots;
            else if (status.alarm == 0)
                alarmCount = 0;

            var machineState = Normalize(status);
            var payload = JsonSerializer.Serialize(new
            {
                aut = status.aut,
                run = status.run,
                motion = status.motion,
                mstb = status.mstb,
                emergency = status.emergency,
                alarm = status.alarm,
                edit = status.edit,
                program = programNumber,
                programName,
                spindle,
                feed,
                alarmCount
            });
            return new FocasControllerStatus(
                machineState, ControllerMode(status.aut), programNumber, programName,
                status.emergency == 1, status.alarm is 1 or 8, alarmCount, spindle, feed, payload);
        }, cancellationToken);

    public Task<int> ReadPartCounterAsync(string source, CancellationToken cancellationToken = default) =>
        thread.RunAsync(() =>
        {
            var current = RequireHandle();
            var parameter = new FocasNative.IODBPSD();
            var number = FocasPartCounterSources.ParameterNumber(source);
            Check(FocasNative.cnc_rdparam(current, number, 0, 8, ref parameter), $"cnc_rdparam {number}");
            if (parameter.ldata < 0)
                throw new FocasException($"FOCAS parameter {number} returned a negative part count.", FocasNative.EW_OK);
            return parameter.ldata;
        }, cancellationToken);

    public Task<IReadOnlyList<string>> ReadProgramHeadAsync(
        string programPath, int byteLimit, int lineLimit, CancellationToken cancellationToken = default) =>
        thread.RunAsync<IReadOnlyList<string>>(() =>
        {
            var current = RequireHandle();
            Check(FocasNative.cnc_upstart4(current, 0, programPath), $"cnc_upstart4 {programPath}");
            var collected = new MemoryStream();
            try
            {
                var buffer = new byte[Math.Clamp(byteLimit, 256, 64 * 1024)];
                var retries = 0;
                while (collected.Length < byteLimit)
                {
                    var length = buffer.Length;
                    var result = FocasNative.cnc_upload4(current, ref length, buffer);
                    if (result == FocasNative.EW_BUFFER)
                    {
                        if (++retries > 20) break;
                        Thread.Sleep(50);
                        continue;
                    }
                    Check(result, "cnc_upload4");
                    if (length <= 0) break;
                    collected.Write(buffer, 0, length);
                    if (Array.IndexOf(buffer, (byte)'%', 1, length - 1) >= 0 && collected.Length > 1) break;
                }
            }
            finally
            {
                _ = FocasNative.cnc_upend4(current);
            }
            var text = Encoding.ASCII.GetString(collected.GetBuffer(), 0, (int)Math.Min(collected.Length, byteLimit));
            return text.Split(["\r\n", "\n", "\r"], StringSplitOptions.None)
                .Take(Math.Max(1, lineLimit))
                .ToArray();
        }, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        try { await DisconnectAsync(); } catch { }
        thread.Dispose();
    }

    private ushort RequireHandle() => handle
        ?? throw new FocasException("The FOCAS session is not connected.", FocasNative.EW_HANDLE);

    private static void Check(short result, string function)
    {
        if (result != FocasNative.EW_OK)
            throw new FocasException($"FOCAS {function} returned {result}.", result);
    }

    private static string Normalize(FocasNative.ODBST status)
    {
        if (status.emergency == 1) return FocasMachineStates.EmergencyStop;
        if (status.alarm is 1 or 8) return FocasMachineStates.Alarm;
        return status.run switch
        {
            3 or 4 => FocasMachineStates.Active,
            2 => FocasMachineStates.FeedHold,
            1 => FocasMachineStates.Stopped,
            _ => FocasMachineStates.Ready
        };
    }

    private static string? ControllerMode(short aut) => aut switch
    {
        0 => "MDI",
        1 => "MEM",
        3 => "EDIT",
        4 => "HND",
        5 => "JOG",
        6 => "TJOG",
        7 => "THND",
        8 => "INC",
        9 => "REF",
        10 => "RMT",
        _ => null
    };

    private static string? Ascii(byte[] value)
    {
        var end = Array.IndexOf(value, (byte)0);
        var text = Encoding.ASCII.GetString(value, 0, end < 0 ? value.Length : end).Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>Executes queued work on one background thread that owns the native handle.</summary>
    private sealed class NativeThread : IDisposable
    {
        private readonly BlockingCollection<Action> queue = new();

        internal NativeThread(string name)
        {
            var thread = new Thread(Run) { IsBackground = true, Name = name };
            thread.Start();
        }

        internal Task<T> RunAsync<T>(Func<T> function, CancellationToken token)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                queue.Add(() =>
                {
                    if (token.IsCancellationRequested)
                    {
                        completion.TrySetCanceled(token);
                        return;
                    }
                    try { completion.TrySetResult(function()); }
                    catch (Exception exception) { completion.TrySetException(exception); }
                });
            }
            catch (InvalidOperationException)
            {
                completion.TrySetException(new ObjectDisposedException(nameof(FocasClient)));
            }
            return completion.Task;
        }

        private void Run()
        {
            foreach (var work in queue.GetConsumingEnumerable()) work();
        }

        public void Dispose() => queue.CompleteAdding();
    }
}
