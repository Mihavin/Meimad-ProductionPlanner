using Meimad.Planner.Server.Domain.Cnc;

namespace Meimad.Planner.Server.Application.Fanuc;

/// <summary>Creates one FOCAS session per Machine worker; the native library is loaded lazily and only on the Server.</summary>
internal interface IFocasClientFactory
{
    IFocasClient Create(FanucFocasConnectionConfiguration configuration);
}

/// <summary>
/// Read-only FANUC FOCAS 2 session. Every member is a bounded read; the contract exposes no
/// macro, parameter, tool, or program write.
/// </summary>
internal interface IFocasClient : IAsyncDisposable
{
    bool Connected { get; }
    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    Task<FocasControllerStatus> ReadStatusAsync(CancellationToken cancellationToken = default);
    Task<int> ReadPartCounterAsync(string source, CancellationToken cancellationToken = default);
    /// <summary>Uploads at most <paramref name="byteLimit"/> bytes of the program at <paramref name="programPath"/> and returns its first lines.</summary>
    Task<IReadOnlyList<string>> ReadProgramHeadAsync(
        string programPath, int byteLimit, int lineLimit, CancellationToken cancellationToken = default);
}

/// <summary>Normalized FOCAS controller status; <see cref="RawPayload"/> is the compact diagnostic form.</summary>
internal sealed record FocasControllerStatus(
    string MachineState,
    string? ControllerMode,
    string? ProgramNumber,
    string? ProgramName,
    bool EmergencyStop,
    bool Alarm,
    int? ActiveAlarmCount,
    decimal? SpindleRpm,
    decimal? FeedRate,
    string RawPayload);

/// <summary>Normalized machine states shared with the MTConnect vocabulary used by the Haas adapter.</summary>
internal static class FocasMachineStates
{
    internal const string Active = "ACTIVE";
    internal const string FeedHold = "FEED_HOLD";
    internal const string Stopped = "STOPPED";
    internal const string Ready = "READY";
    internal const string Alarm = "ALARM";
    internal const string EmergencyStop = "EMERGENCY_STOP";
}

/// <summary>A FOCAS call returned a non-zero result code.</summary>
internal sealed class FocasException(string message, short code) : Exception(message)
{
    internal short Code { get; } = code;
}

/// <summary>The FOCAS library (Fwlib) is not installed next to the Server.</summary>
internal sealed class FocasLibraryUnavailableException(string message) : Exception(message);
