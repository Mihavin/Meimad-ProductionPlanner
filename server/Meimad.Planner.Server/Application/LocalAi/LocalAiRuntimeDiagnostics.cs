using Meimad.Planner.Server.Configuration;
namespace Meimad.Planner.Server.Application.LocalAi;
internal sealed record LocalAiRuntimeState(string Status, DateTimeOffset? LastAttemptAt,
    DateTimeOffset? LastSucceededAt, string Detail);
internal sealed class LocalAiRuntimeDiagnostics(LocalAiOptions options, TimeProvider clock)
{
    private LocalAiRuntimeState state = new(options.Enabled ? "NOT_PROBED" : "DISABLED", null, null,
        "Deterministic explanations remain available. Runtime contact is not inferred from configuration.");
    internal LocalAiRuntimeState State => Volatile.Read(ref state);
    private volatile bool suspended;
    internal void Suspend() { suspended = true; Volatile.Write(ref state, State with { Status = "SUSPENDED", Detail = "Local inference is suspended by an administrator. Deterministic evidence remains available." }); }
    /// <summary>The enabled/pinned setting changed; the runtime has not been contacted since.</summary>
    internal void Reconfigure()
    {
        if (!suspended) Volatile.Write(ref state, State with { Status = options.Enabled ? "NOT_PROBED" : "DISABLED", Detail = "Runtime availability has not been checked since the AI settings changed." });
    }
    internal void Resume() { suspended = false; Volatile.Write(ref state, State with { Status = options.Enabled ? "NOT_PROBED" : "DISABLED", Detail = "Runtime availability has not been checked since the control changed." }); }
    internal void Begin() => Volatile.Write(ref state, State with { Status = "RUNNING", LastAttemptAt = clock.GetUtcNow(), Detail = "A bounded local model request is running." });
    internal void End(bool success, string code) => Volatile.Write(ref state, State with
    { Status = suspended ? "SUSPENDED" : code, LastSucceededAt = success ? clock.GetUtcNow() : State.LastSucceededAt,
        Detail = success ? "Pinned runtime/model returned a validated claim ordering. This does not establish production authority."
            : "The latest runtime attempt did not produce an accepted answer; deterministic evidence remains available." });
}
