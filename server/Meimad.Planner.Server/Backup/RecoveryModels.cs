namespace Meimad.Planner.Server.Backup;

internal sealed record RecoveryFile(string Path, long Length, string Sha256);
internal sealed record RecoveryDependency(string CaseId, string WorkingFolderPath);
internal sealed record RecoveryManifest(int FormatVersion, string SetId, DateTimeOffset SnapshotAt,
    int SchemaVersion, string ApplicationVersion, string RequestedBy, IReadOnlyList<RecoveryFile> Files,
    IReadOnlyList<RecoveryDependency> EngineeringDependencies, string KeyProbe);
internal sealed record RecoverySetResult(string FileName, string SetId, DateTimeOffset SnapshotAt,
    DateTimeOffset VerifiedAt, int SchemaVersion, long ByteLength, string Sha256, int FileCount, int ExternalFolderCount);
internal sealed record RecoveryStatus(bool Configured, string? Destination, string ScheduleOwner,
    RecoverySetResult? LastComplete, double? AgeHours, string? LastAttemptResult);
internal sealed record RecoveryRestoreResult(string Directory, RecoveryManifest Manifest, string ActivationNotice);

internal sealed class RecoveryOptions
{
    internal string? Folder { get; init; }
    internal string? PasswordFile { get; init; }
    internal bool IndependentDestinationConfirmed { get; init; }
    internal bool Configured => !string.IsNullOrWhiteSpace(Folder) && !string.IsNullOrWhiteSpace(PasswordFile)
        && IndependentDestinationConfirmed;
    internal static RecoveryOptions Read(IConfiguration configuration) => new()
    {
        Folder = configuration["Recovery:Folder"],
        PasswordFile = configuration["Recovery:PasswordFile"],
        IndependentDestinationConfirmed = configuration.GetValue<bool>("Recovery:IndependentDestinationConfirmed")
    };
}

// Every immutable-artifact cleanup path participates. Acquire before taking the DB snapshot.
internal static class RecoveryArtifactLease
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    internal static async Task<IDisposable> AcquireAsync(CancellationToken token)
    { await Gate.WaitAsync(token); return new Lease(); }
    internal static IDisposable Acquire() { Gate.Wait(); return new Lease(); }
    private sealed class Lease : IDisposable
    {
        private bool disposed;
        public void Dispose() { if (!disposed) { disposed = true; Gate.Release(); } }
    }
}
