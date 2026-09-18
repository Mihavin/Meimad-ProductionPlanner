using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Meimad.Planner.Server.Configuration;

namespace Meimad.Planner.Server.Application.ClientInstaller;

/// <summary>What the Server tells a client about the client installer it can distribute.</summary>
public sealed record ClientInstallerManifest(
    string ServerVersion,
    string? ClientVersion,
    bool InstallerAvailable,
    string? FileName,
    long? ByteLength,
    string? Sha256,
    DateTimeOffset? BuiltAt);

public sealed record ClientInstallerFile(
    string Path,
    string FileName,
    string ClientVersion,
    long ByteLength,
    string Sha256);

/// <summary>
/// Serves the Windows client MSI bundled with the Server so that a client whose version does
/// not match the Server's can download and install the matching one. The MSI is trusted only
/// when its JSON manifest (written by the installer build) exists, parses, and describes the
/// file on disk; the SHA-256 is computed from the file itself and cached until the file changes.
/// </summary>
public sealed class ClientInstallerService(
    ClientInstallerOptions options,
    ILogger<ClientInstallerService>? logger = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object gate = new();
    private CachedInstaller? cache;

    /// <summary>The running Server's version in <c>major.minor.patch</c> form.</summary>
    public static string ServerVersion { get; } = ResolveServerVersion();

    public ClientInstallerManifest GetManifest()
    {
        var installer = Resolve();
        return installer is null
            ? new ClientInstallerManifest(ServerVersion, null, false, null, null, null, null)
            : new ClientInstallerManifest(
                ServerVersion,
                installer.ClientVersion,
                true,
                installer.FileName,
                installer.ByteLength,
                installer.Sha256,
                installer.BuiltAt);
    }

    public ClientInstallerFile? GetInstaller()
    {
        var installer = Resolve();
        return installer is null
            ? null
            : new ClientInstallerFile(
                installer.Path, installer.FileName, installer.ClientVersion, installer.ByteLength, installer.Sha256);
    }

    /// <summary>Reduces "0.1.117+abc", "0.1.117-beta", or "0.1.117.0" to "0.1.117".</summary>
    internal static string NormalizeVersion(string? value)
    {
        var core = (value ?? string.Empty).Split('+', '-')[0].Trim();
        if (Version.TryParse(core, out var version))
        {
            return $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";
        }

        return core.Length == 0 ? "0.0.0" : core;
    }

    private CachedInstaller? Resolve()
    {
        var path = options.InstallerPath;
        if (!File.Exists(path))
        {
            return null;
        }

        var file = new FileInfo(path);
        lock (gate)
        {
            if (cache is not null
                && string.Equals(cache.Path, path, StringComparison.Ordinal)
                && cache.LastWriteUtc == file.LastWriteTimeUtc
                && cache.ByteLength == file.Length)
            {
                return cache;
            }
        }

        var manifestPath = options.ManifestPath;
        if (!File.Exists(manifestPath))
        {
            logger?.LogWarning(
                "Client installer {Path} has no manifest {Manifest}; it is not offered to clients.",
                path, manifestPath);
            return null;
        }

        SidecarManifest? sidecar;
        try
        {
            sidecar = JsonSerializer.Deserialize<SidecarManifest>(File.ReadAllText(manifestPath), JsonOptions);
        }
        catch (JsonException exception)
        {
            logger?.LogWarning(exception, "Client installer manifest {Manifest} is not valid JSON.", manifestPath);
            return null;
        }

        if (sidecar is null || string.IsNullOrWhiteSpace(sidecar.Version)
            || !Version.TryParse(sidecar.Version.Trim(), out _))
        {
            logger?.LogWarning("Client installer manifest {Manifest} has no valid version.", manifestPath);
            return null;
        }

        string sha256;
        using (var stream = File.OpenRead(path))
        {
            sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }

        if (!string.IsNullOrWhiteSpace(sidecar.Sha256)
            && !string.Equals(sidecar.Sha256.Trim(), sha256, StringComparison.OrdinalIgnoreCase))
        {
            logger?.LogWarning(
                "Client installer {Path} does not match the SHA-256 in its manifest; it is not offered to clients.",
                path);
            return null;
        }

        if (sidecar.ByteLength is { } declaredLength && declaredLength != file.Length)
        {
            logger?.LogWarning(
                "Client installer {Path} is {Actual} bytes but its manifest declares {Declared}; it is not offered to clients.",
                path, file.Length, declaredLength);
            return null;
        }

        var resolved = new CachedInstaller(
            path,
            options.FileName,
            NormalizeVersion(sidecar.Version),
            file.Length,
            sha256,
            sidecar.BuiltAt,
            file.LastWriteTimeUtc);
        lock (gate)
        {
            cache = resolved;
        }

        logger?.LogInformation(
            "Client installer {Version} ({Bytes} bytes) is available for distribution from {Path}.",
            resolved.ClientVersion, resolved.ByteLength, path);
        return resolved;
    }

    private static string ResolveServerVersion()
    {
        var assembly = typeof(ClientInstallerService).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var value = string.IsNullOrWhiteSpace(informational)
            ? assembly.GetName().Version?.ToString()
            : informational;
        return NormalizeVersion(value);
    }

    private sealed record SidecarManifest(
        string? FileName,
        string? Version,
        string? Sha256,
        long? ByteLength,
        DateTimeOffset? BuiltAt);

    private sealed record CachedInstaller(
        string Path,
        string FileName,
        string ClientVersion,
        long ByteLength,
        string Sha256,
        DateTimeOffset? BuiltAt,
        DateTime LastWriteUtc);
}
