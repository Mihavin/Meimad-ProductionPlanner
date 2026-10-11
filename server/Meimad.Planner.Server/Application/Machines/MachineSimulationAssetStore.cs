using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Configuration;

namespace Meimad.Planner.Server.Application.Machines;

internal sealed class MachineSimulationAssetException(string code, string message) : Exception(message)
{
    internal string Code { get; } = code;
}

/// <summary>
/// The Server's Machine mesh store (schema v114): uploaded GLB (glTF 2 binary) and STL files are hashed and stored once
/// per Machine below the release root as <c>machine-assets/&lt;machine&gt;/&lt;sha256&gt;.&lt;ext&gt;</c>, so the
/// recovery set backs them up with the releases. Clients refer to them by asset id only.
/// </summary>
internal sealed class MachineSimulationAssetStore(
    IMachineSimulationRepository repository,
    GCodeOptions options,
    TimeProvider timeProvider)
{
    internal const long MaximumBytes = 200L * 1024 * 1024;
    internal const string Folder = "machine-assets";
    internal const string GlbMediaType = "model/gltf-binary";
    internal const string StlMediaType = "model/stl";

    /// <summary>Stores the uploaded <paramref name="content"/>; null when the Machine does not exist.</summary>
    public async Task<MachineSimulationAsset?> AddAsync(
        string machineId,
        string? fileName,
        Stream content,
        EditAuthority authority,
        CancellationToken cancellationToken)
    {
        var actor = SignedInActor.Require(authority);
        machineId = machineId.Trim();
        var name = Path.GetFileName((fileName ?? string.Empty).Trim());
        var extension = Path.GetExtension(name).ToLowerInvariant();
        if (name.Length is 0 or > 255 || extension is not (".glb" or ".stl"))
            throw new MachineSimulationAssetException("unsupported_asset_type", "Upload a .glb (glTF 2 binary) or .stl file.");
        if (await repository.ListAssetsAsync(machineId, cancellationToken) is null) return null;

        var root = Path.GetFullPath(Path.Combine(options.ResolvedReleaseRoot, Folder));
        var incoming = Path.Combine(root, ".incoming");
        Directory.CreateDirectory(incoming);
        var temporary = Path.Combine(incoming, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            long length;
            string hash;
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                length = 0;
                int read;
                while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    length += read;
                    if (length > MaximumBytes)
                        throw new MachineSimulationAssetException("asset_too_large", $"A Machine mesh may be at most {MaximumBytes / (1024 * 1024)} MB.");
                    sha.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                if (length == 0) throw new MachineSimulationAssetException("asset_empty", "The uploaded file is empty.");
                hash = Convert.ToHexStringLower(sha.GetHashAndReset());
                file.Position = 0;
                await RequireFormatAsync(file, extension, length, cancellationToken);
            }

            var folder = Path.Combine(root, FolderName(machineId));
            Directory.CreateDirectory(folder);
            var target = Path.Combine(folder, hash + extension);
            if (!File.Exists(target)) File.Move(temporary, target);
            var relative = Path.GetRelativePath(options.ResolvedReleaseRoot, target).Replace('\\', '/');
            return await repository.AddAssetAsync(new MachineSimulationAsset(
                Guid.NewGuid().ToString("N"),
                machineId,
                name,
                extension == ".glb" ? GlbMediaType : StlMediaType,
                length,
                hash,
                relative,
                timeProvider.GetUtcNow(),
                actor), cancellationToken);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>The stored file of an asset, or null when it is missing on disk.</summary>
    public string? PathOf(MachineSimulationAsset asset)
    {
        var root = Path.GetFullPath(options.ResolvedReleaseRoot);
        var full = Path.GetFullPath(Path.Combine(root, asset.StoredRelativePath));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && File.Exists(full) ? full : null;
    }

    /// <summary>A file-system-safe folder per Machine: the id itself when it is plain, else a hash of it.</summary>
    internal static string FolderName(string machineId) =>
        machineId.Length is > 0 and <= 64 && machineId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            ? machineId
            : "m-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(machineId)))[..32];

    /// <summary>GLB: glTF magic, version 2 and the header length equal to the file length. STL: binary with a matching triangle count, or ASCII starting with "solid".</summary>
    private static async Task RequireFormatAsync(Stream file, string extension, long length, CancellationToken cancellationToken)
    {
        var header = new byte[84];
        var read = await file.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken);
        if (extension == ".glb")
        {
            if (read < 12
                || BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x46546C67
                || BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)) != 2
                || BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8)) != length)
                throw new MachineSimulationAssetException("invalid_asset_content", "The file is not a glTF 2 binary (.glb) file.");
            return;
        }
        if (read == 84 && 84 + 50L * BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(80)) == length) return;
        if (Encoding.ASCII.GetString(header, 0, read).TrimStart().StartsWith("solid", StringComparison.OrdinalIgnoreCase)) return;
        throw new MachineSimulationAssetException("invalid_asset_content", "The file is not a binary or ASCII STL file.");
    }
}
