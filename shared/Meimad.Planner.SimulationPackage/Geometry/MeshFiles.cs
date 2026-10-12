using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Meimad.Planner.SimulationPackage.Coordinates;

namespace Meimad.Planner.SimulationPackage.Geometry;

/// <summary>The outcome of reading a mesh/CAD file: readable or not, its format, triangles and bounds.</summary>
public sealed record MeshFileInfo(bool Readable, string Format, int TriangleCount, BoundingBox? Bounds, string? Problem);

/// <summary>
/// Reads the geometry files a package may carry without a CAD kernel: binary and ASCII STL (triangles
/// and bounding box) and STEP (ISO-10303-21 header only).
/// </summary>
public static class MeshFiles
{
    public const string Stl = "stl";
    public const string Step = "step";

    public static string? FormatOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".stl" => Stl,
        ".stp" or ".step" => Step,
        _ => null
    };

    public static MeshFileInfo Read(string path)
    {
        var format = FormatOf(path);
        try
        {
            return format switch
            {
                Stl => ReadStl(File.ReadAllBytes(path)),
                Step => ReadStepHeader(path),
                _ => new MeshFileInfo(false, "unknown", 0, null, "Only STL and STEP geometry is supported.")
            };
        }
        catch (IOException exception)
        {
            return new MeshFileInfo(false, format ?? "unknown", 0, null, exception.Message);
        }
        catch (UnauthorizedAccessException exception)
        {
            return new MeshFileInfo(false, format ?? "unknown", 0, null, exception.Message);
        }
    }

    /// <summary>Reads an STL from bytes: binary when the size matches the triangle count, else ASCII.</summary>
    public static MeshFileInfo ReadStl(byte[] bytes)
    {
        if (bytes.Length >= 84)
        {
            var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(80, 4));
            if (count > 0 && 84L + 50L * count == bytes.Length) return ReadBinaryStl(bytes, (int)count);
        }

        var head = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 512)).TrimStart();
        if (head.StartsWith("solid", StringComparison.OrdinalIgnoreCase)) return ReadAsciiStl(bytes);
        return new MeshFileInfo(false, Stl, 0, null,
            bytes.Length < 84 ? "The file is too short to be an STL." : "The STL is neither a complete binary STL nor an ASCII STL.");
    }

    private static MeshFileInfo ReadBinaryStl(byte[] bytes, int count)
    {
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        for (var triangle = 0; triangle < count; triangle++)
        {
            var offset = 84 + triangle * 50 + 12;
            for (var vertex = 0; vertex < 3; vertex++, offset += 12)
            {
                var x = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(offset, 4));
                var y = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(offset + 4, 4));
                var z = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(offset + 8, 4));
                if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
                    return new MeshFileInfo(false, Stl, 0, null, "The STL contains a non-finite coordinate.");
                minX = Math.Min(minX, x); minY = Math.Min(minY, y); minZ = Math.Min(minZ, z);
                maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y); maxZ = Math.Max(maxZ, z);
            }
        }

        return new MeshFileInfo(true, Stl, count, new BoundingBox(new(minX, minY, minZ), new(maxX, maxY, maxZ)), null);
    }

    private static MeshFileInfo ReadAsciiStl(byte[] bytes)
    {
        var text = Encoding.ASCII.GetString(bytes);
        var vertices = 0;
        var facets = 0;
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("facet", StringComparison.OrdinalIgnoreCase)) facets++;
            if (!line.StartsWith("vertex", StringComparison.OrdinalIgnoreCase)) continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 4
                || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
                || !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var z)
                || !double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z))
            {
                return new MeshFileInfo(false, Stl, 0, null, "An ASCII STL vertex line is malformed.");
            }

            vertices++;
            minX = Math.Min(minX, x); minY = Math.Min(minY, y); minZ = Math.Min(minZ, z);
            maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y); maxZ = Math.Max(maxZ, z);
        }

        if (vertices == 0 || vertices % 3 != 0 || facets != vertices / 3)
            return new MeshFileInfo(false, Stl, 0, null, "The ASCII STL has no complete triangles.");
        return new MeshFileInfo(true, Stl, facets, new BoundingBox(new(minX, minY, minZ), new(maxX, maxY, maxZ)), null);
    }

    private static MeshFileInfo ReadStepHeader(string path)
    {
        using var stream = File.OpenRead(path);
        var buffer = new byte[256];
        var read = stream.Read(buffer, 0, buffer.Length);
        var head = Encoding.ASCII.GetString(buffer, 0, read).TrimStart();
        return head.StartsWith("ISO-10303-21", StringComparison.Ordinal)
            ? new MeshFileInfo(true, Step, 0, null, null)
            : new MeshFileInfo(false, Step, 0, null, "The STEP file does not start with ISO-10303-21.");
    }

    /// <summary>Writes a binary STL (used by tests and the fake exporter).</summary>
    public static byte[] WriteBinaryStl(IReadOnlyList<(Vector3d A, Vector3d B, Vector3d C)> triangles, string header = "Meimad")
    {
        var bytes = new byte[84 + 50 * triangles.Count];
        Encoding.ASCII.GetBytes(header.Length > 80 ? header[..80] : header).CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(80, 4), (uint)triangles.Count);
        var offset = 84;
        foreach (var (a, b, c) in triangles)
        {
            offset += 12; // zero normal: readers recompute it
            foreach (var p in new[] { a, b, c })
            {
                BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset, 4), (float)p.X);
                BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset + 4, 4), (float)p.Y);
                BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset + 8, 4), (float)p.Z);
                offset += 12;
            }

            offset += 2;
        }

        return bytes;
    }

    /// <summary>The twelve triangles of an axis-aligned box (binary STL helper).</summary>
    public static IReadOnlyList<(Vector3d, Vector3d, Vector3d)> Box(Vector3d min, Vector3d max)
    {
        Vector3d P(int i) => new((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z);
        int[][] faces = [[0, 2, 3, 1], [4, 5, 7, 6], [0, 1, 5, 4], [2, 6, 7, 3], [0, 4, 6, 2], [1, 3, 7, 5]];
        var triangles = new List<(Vector3d, Vector3d, Vector3d)>();
        foreach (var f in faces)
        {
            triangles.Add((P(f[0]), P(f[1]), P(f[2])));
            triangles.Add((P(f[0]), P(f[2]), P(f[3])));
        }

        return triangles;
    }
}

/// <summary>SHA-256 helpers (lowercase hex, the form every manifest and Server table uses).</summary>
public static class PackageHashing
{
    public static string Sha256OfFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    public static string Sha256OfBytes(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
