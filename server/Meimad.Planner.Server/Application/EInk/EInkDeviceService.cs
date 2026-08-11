using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Meimad.Planner.Server.Application.Timeline;
using Meimad.Planner.Server.Configuration;

namespace Meimad.Planner.Server.Application.EInk;

internal sealed class EInkDeviceService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IEInkDeviceRepository repository;
    private readonly TimelineProjectionService timelineService;
    private readonly EInkOptions options;
    private readonly TimeProvider timeProvider;

    public EInkDeviceService(
        IEInkDeviceRepository repository,
        TimelineProjectionService timelineService,
        EInkOptions options,
        TimeProvider timeProvider)
    {
        this.repository = repository;
        this.timelineService = timelineService;
        this.options = options;
        this.timeProvider = timeProvider;
    }

    internal async Task<EInkResource<EInkVersionResponse>> ReadVersionAsync(
        string deviceId,
        string token,
        CancellationToken cancellationToken = default)
    {
        var source = await AuthorizedAsync(deviceId, token, cancellationToken);
        var screenRevision = Revision("screen", source.RevisionSeed);
        var timeRevision = TimeRevision();
        var value = new EInkVersionResponse(
            1,
            source.DeviceId,
            source.MachineId,
            screenRevision,
            source.Package is null
                ? null
                : new EInkVersionPackage(source.Package.PackageId, source.Package.Revision),
            timeRevision);
        return new EInkResource<EInkVersionResponse>(value, EntityTag(value));
    }

    internal async Task<EInkResource<EInkMachineScreenResponse>> ReadMachineScreenAsync(
        string deviceId,
        string token,
        CancellationToken cancellationToken = default)
    {
        var source = await AuthorizedAsync(deviceId, token, cancellationToken);
        var now = timeProvider.GetUtcNow();
        TimelineProjection? timeline = null;
        if (source.MachineId is not null)
        {
            var start = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
            timeline = await timelineService.CalculateAsync(start, start.AddDays(7), cancellationToken);
        }

        var finishes = timeline?.Machines
            .SelectMany(machine => machine.Intervals)
            .Where(interval => interval.OperationId is not null
                && interval.Type is "setup" or "production" or "reserved")
            .GroupBy(interval => interval.OperationId!, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (DateTimeOffset?)group.Max(interval => interval.EndsAt),
                StringComparer.Ordinal)
            ?? new Dictionary<string, DateTimeOffset?>(StringComparer.Ordinal);
        var unfinished = source.Backlog
            .Where(operation => operation.Status is not "complete" and not "cancelled")
            .OrderBy(operation => operation.BacklogPosition)
            .ToArray();
        var current = unfinished.FirstOrDefault();
        var conflicts = timeline?.Conflicts
            .Where(conflict => source.MachineId is not null
                && conflict.MachineIds.Contains(source.MachineId, StringComparer.Ordinal))
            .Select(conflict => new EInkConflictResponse(
                conflict.Code,
                conflict.Severity,
                conflict.Message))
            .ToArray()
            ?? [];
        var status = Status(source, current, conflicts);
        var screenRevision = Revision("screen", source.RevisionSeed);
        var value = new EInkMachineScreenResponse(
            1,
            source.DeviceId,
            screenRevision,
            now,
            source.Machine is null ? null : new EInkMachineResponse(
                source.Machine.MachineId,
                source.Machine.Number,
                source.Machine.Name,
                source.Machine.ProcessType),
            status,
            Job(current, finishes),
            unfinished.Skip(1).Take(3).Select(operation => Job(operation, finishes)!).ToArray(),
            conflicts,
            source.Package is null ? null : new EInkScreenPackageResponse(
                source.Package.PackageId,
                source.Package.Revision,
                ManifestPath(source.DeviceId, source.Package)));
        return new EInkResource<EInkMachineScreenResponse>(value, EntityTag(value));
    }

    internal async Task<EInkResource<EInkManifestResponse>> ReadCurrentManifestAsync(
        string deviceId,
        string token,
        CancellationToken cancellationToken = default)
    {
        var source = await AuthorizedAsync(deviceId, token, cancellationToken);
        return Manifest(source, source.Package
            ?? throw new EInkPackageNotAssignedException());
    }

    internal async Task<EInkResource<EInkManifestResponse>> ReadExactManifestAsync(
        string deviceId,
        string packageId,
        string revision,
        string token,
        CancellationToken cancellationToken = default)
    {
        var source = await AuthorizedAsync(deviceId, token, cancellationToken);
        var package = ExactPackage(source, packageId, revision);
        return Manifest(source, package);
    }

    internal async Task<EInkFileDownload> ResolveFileAsync(
        string deviceId,
        string packageId,
        string revision,
        string fileId,
        string token,
        CancellationToken cancellationToken = default)
    {
        var source = await AuthorizedAsync(deviceId, token, cancellationToken);
        var package = ExactPackage(source, packageId, revision);
        var file = package.Files.SingleOrDefault(value => value.FileId == fileId)
            ?? throw new EInkDeviceResourceNotFoundException();
        var fullPath = ResolveStoragePath(file.StorageRelativePath);
        if (!File.Exists(fullPath))
        {
            throw new EInkPackageFileIntegrityException("The authorized package file is unavailable.");
        }

        var info = new FileInfo(fullPath);
        if (info.Length != file.ByteLength)
        {
            throw new EInkPackageFileIntegrityException("The package file length does not match its manifest.");
        }

        await using var stream = File.OpenRead(fullPath);
        var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken))
            .ToLowerInvariant();
        if (!FixedEquals(actualHash, file.Sha256))
        {
            throw new EInkPackageFileIntegrityException("The package file checksum does not match its manifest.");
        }

        return new EInkFileDownload(fullPath, file.MediaType, file.ByteLength, file.Sha256);
    }

    internal async Task<EInkResource<EInkTimeConfigResponse>> ReadTimeConfigAsync(
        string deviceId,
        string token,
        CancellationToken cancellationToken = default)
    {
        _ = await AuthorizedAsync(deviceId, token, cancellationToken);
        var value = new EInkTimeConfigResponse(
            1,
            TimeRevision(),
            options.TimeZoneId,
            options.Workdays,
            [new EInkShiftWindowResponse(
                options.ShiftStartsAtLocal,
                options.ShiftEndsAtLocal)],
            options.PollIntervalSeconds,
            new EInkRetryResponse(
                options.MaximumRetryAttempts,
                options.InitialBackoffSeconds),
            []);
        return new EInkResource<EInkTimeConfigResponse>(value, EntityTag(value));
    }

    private async Task<EInkDeviceSource> AuthorizedAsync(
        string deviceId,
        string token,
        CancellationToken cancellationToken)
    {
        var source = await repository.ReadAsync(deviceId, cancellationToken);
        var suppliedHash = HashToken(token);
        if (source is null
            || !source.IsEnabled
            || string.IsNullOrWhiteSpace(source.CredentialHash)
            || !FixedEquals(suppliedHash, source.CredentialHash))
        {
            throw new EInkDeviceResourceNotFoundException();
        }

        return source;
    }

    private EInkResource<EInkManifestResponse> Manifest(
        EInkDeviceSource source,
        EInkPackageSource package)
    {
        if (source.MachineId is null)
        {
            throw new EInkDeviceResourceNotFoundException();
        }

        var value = new EInkManifestResponse(
            1,
            package.PackageId,
            package.Revision,
            source.MachineId,
            package.BatchId,
            package.BatchOperationId,
            package.ToolCartId,
            package.PublishedAt,
            ManifestMetadata(package.Metadata),
            package.Files.Select(file => ManifestFile(source, package, file)).ToArray());
        return new EInkResource<EInkManifestResponse>(value, EntityTag(value));
    }

    private static EInkPackageSource ExactPackage(
        EInkDeviceSource source,
        string packageId,
        string revision)
    {
        return source.Package is not null
            && source.Package.PackageId == packageId
            && source.Package.Revision == revision
                ? source.Package
                : throw new EInkDeviceResourceNotFoundException();
    }

    private static EInkManifestFileResponse ManifestFile(
        EInkDeviceSource source,
        EInkPackageSource package,
        EInkPackageFileSource file)
    {
        var normalized = file.LogicalPath.Replace('\\', '/');
        if (normalized.StartsWith('/')
            || normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => segment is "." or ".."))
        {
            throw new EInkPackageFileIntegrityException(
                "The package manifest contains an unsafe logical path.");
        }

        return new EInkManifestFileResponse(
            file.FileId,
            file.AssetType,
            normalized,
            FilePath(source.DeviceId, package, file.FileId),
            file.MediaType,
            file.ByteLength,
            file.ModifiedAt,
            new EInkChecksumResponse("sha-256", file.Sha256));
    }

    private static EInkManifestMetadataResponse? ManifestMetadata(
        EInkPackageMetadataSource? value) => value is null
        ? null
        : new EInkManifestMetadataResponse(
            new EInkManifestMachineResponse(
                value.MachineId,
                value.MachineNumber,
                value.MachineName),
            new EInkManifestPartResponse(
                value.CaseId,
                value.PartNumber,
                value.PartName,
                value.PartRevision,
                value.Customer),
            new EInkManifestBatchResponse(
                value.BatchId,
                value.BatchNumber,
                value.PlannedQuantity),
            new EInkManifestOperationResponse(
                value.BatchOperationId,
                value.OperationNumber,
                value.OperationName));

    private string ResolveStoragePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathRooted(relativePath))
        {
            throw new EInkPackageFileIntegrityException("The package storage path is invalid.");
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.ResolvedPackageRoot));
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        var relative = Path.GetRelativePath(root, fullPath);
        if (Path.IsPathRooted(relative)
            || relative == "."
            || relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new EInkPackageFileIntegrityException("The package storage path escapes the configured root.");
        }

        return fullPath;
    }

    private static EInkStatusResponse Status(
        EInkDeviceSource source,
        EInkOperationSource? current,
        IReadOnlyList<EInkConflictResponse> conflicts)
    {
        if (source.Machine is null)
        {
            return new EInkStatusResponse("unassigned", "No Machine assigned", "■", "#9E9E9E");
        }

        if (!source.Machine.IsActive)
        {
            return new EInkStatusResponse("unavailable", "Machine unavailable", "■", "#9E9E9E");
        }

        if (conflicts.Any(conflict => conflict.Severity == "blocking"))
        {
            return new EInkStatusResponse("conflict", "Blocking conflict", "▲", "#C62828");
        }

        return current is null
            ? new EInkStatusResponse("idle", "Idle / no work", "■", "#9E9E9E")
            : new EInkStatusResponse("current", "Current job", "▶", "#1E88E5");
    }

    private static EInkJobResponse? Job(
        EInkOperationSource? operation,
        IReadOnlyDictionary<string, DateTimeOffset?> finishes) => operation is null
        ? null
        : new EInkJobResponse(
            operation.PartNumber,
            operation.BatchNumber,
            operation.OperationId,
            operation.OperationNumber,
            operation.OperationName,
            operation.PlannedQuantity,
            operation.Status,
            finishes.GetValueOrDefault(operation.OperationId));

    private static string ManifestPath(string deviceId, EInkPackageSource package) =>
        $"/api/v1/eink/devices/{Uri.EscapeDataString(deviceId)}/packages/{Uri.EscapeDataString(package.PackageId)}/revisions/{Uri.EscapeDataString(package.Revision)}/manifest";

    private static string FilePath(
        string deviceId,
        EInkPackageSource package,
        string fileId) =>
        $"/api/v1/eink/devices/{Uri.EscapeDataString(deviceId)}/packages/{Uri.EscapeDataString(package.PackageId)}/revisions/{Uri.EscapeDataString(package.Revision)}/files/{Uri.EscapeDataString(fileId)}";

    private string TimeRevision() => Revision("time", JsonSerializer.Serialize(new
    {
        options.TimeZoneId,
        options.Workdays,
        options.ShiftStartsAtLocal,
        options.ShiftEndsAtLocal,
        options.PollIntervalSeconds,
        options.MaximumRetryAttempts,
        options.InitialBackoffSeconds
    }, JsonOptions));

    private static string HashToken(string token) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private static bool FixedEquals(string left, string right)
    {
        var leftBytes = Encoding.ASCII.GetBytes(left);
        var rightBytes = Encoding.ASCII.GetBytes(right);
        return leftBytes.Length == rightBytes.Length
            && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static string Revision(string prefix, string value)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
        return $"{prefix}-{hash[..16]}";
    }

    private static string EntityTag<T>(T value) =>
        $"\"{Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions))).ToLowerInvariant()}\"";
}

internal sealed class EInkDeviceResourceNotFoundException : Exception;

internal sealed class EInkPackageNotAssignedException : Exception;

internal sealed class EInkPackageFileIntegrityException : Exception
{
    internal EInkPackageFileIntegrityException(string message) : base(message) { }
}
