using System.Globalization;
using System.IO;
using System.Text;

namespace Meimad.Planner.Client.Windows.Views;

/// <summary>
/// Reads binary and ASCII STL files into the viewer's mesh model. STL carries triangles only, so
/// the result has no B-rep faces or edges; feature edges are derived from creases by the viewer.
/// </summary>
internal static class StlMeshLoader
{
    private const int MaximumTriangles = 500_000;
    private const long MaximumStlBytes = 256L * 1024 * 1024;

    public static StepModelData Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("The STL file was not found.", path);
        }
        if (info.Length > MaximumStlBytes)
        {
            throw new InvalidDataException("The STL viewer supports files up to 256 MiB.");
        }

        var bytes = File.ReadAllBytes(path);
        return IsBinary(bytes) ? ParseBinary(bytes) : ParseAscii(bytes);
    }

    internal static StepModelData Parse(byte[] bytes) => IsBinary(bytes) ? ParseBinary(bytes) : ParseAscii(bytes);

    private static bool IsBinary(byte[] bytes)
    {
        if (bytes.Length < 84)
        {
            return false;
        }

        var declared = BitConverter.ToUInt32(bytes, 80);
        if (84L + declared * 50L == bytes.Length)
        {
            return true;
        }

        // Some exporters write "solid" into a binary header; trust the size arithmetic first and
        // only then the keyword.
        var head = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 5));
        return !head.StartsWith("solid", StringComparison.OrdinalIgnoreCase);
    }

    private static StepModelData ParseBinary(byte[] bytes)
    {
        var declared = BitConverter.ToUInt32(bytes, 80);
        var available = (bytes.Length - 84) / 50;
        var count = (int)Math.Min(declared, (uint)available);
        if (count <= 0)
        {
            throw new InvalidDataException("The STL file contains no triangles.");
        }
        if (count > MaximumTriangles)
        {
            throw new InvalidDataException($"The STL mesh exceeds the {MaximumTriangles:N0}-triangle viewer limit.");
        }

        var builder = new MeshBuilder();
        var offset = 84;
        for (var index = 0; index < count; index++)
        {
            // 12 bytes normal (ignored; recomputed from winding), 3 × 12 bytes vertices, 2 bytes attribute.
            var a = ReadVertex(bytes, offset + 12);
            var b = ReadVertex(bytes, offset + 24);
            var c = ReadVertex(bytes, offset + 36);
            builder.AddTriangle(a, b, c);
            offset += 50;
        }

        return builder.Build();
    }

    private static StepPoint3 ReadVertex(byte[] bytes, int offset) => new(
        BitConverter.ToSingle(bytes, offset),
        BitConverter.ToSingle(bytes, offset + 4),
        BitConverter.ToSingle(bytes, offset + 8));

    private static StepModelData ParseAscii(byte[] bytes)
    {
        var text = Encoding.ASCII.GetString(bytes);
        var builder = new MeshBuilder();
        var pending = new List<StepPoint3>(3);
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("vertex", StringComparison.OrdinalIgnoreCase))
            {
                if (line.StartsWith("endfacet", StringComparison.OrdinalIgnoreCase))
                {
                    pending.Clear();
                }
                continue;
            }

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4
                || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
                || !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
            {
                throw new InvalidDataException("The ASCII STL file has a malformed vertex line.");
            }

            pending.Add(new StepPoint3(x, y, z));
            if (pending.Count == 3)
            {
                builder.AddTriangle(pending[0], pending[1], pending[2]);
                pending.Clear();
                if (builder.TriangleCount > MaximumTriangles)
                {
                    throw new InvalidDataException($"The STL mesh exceeds the {MaximumTriangles:N0}-triangle viewer limit.");
                }
            }
        }

        if (builder.TriangleCount == 0)
        {
            throw new InvalidDataException("The STL file contains no triangles.");
        }

        return builder.Build();
    }

    private sealed class MeshBuilder
    {
        private readonly List<StepPoint3> points = [];
        private readonly List<StepTriangle3> triangles = [];
        private readonly Dictionary<StepPoint3, int> indexByPoint = [];

        internal int TriangleCount => triangles.Count;

        internal void AddTriangle(StepPoint3 a, StepPoint3 b, StepPoint3 c)
        {
            var first = Index(a);
            var second = Index(b);
            var third = Index(c);
            if (first == second || second == third || first == third)
            {
                return;
            }
            triangles.Add(new StepTriangle3(first, second, third));
        }

        private int Index(StepPoint3 point)
        {
            if (!indexByPoint.TryGetValue(point, out var index))
            {
                index = points.Count;
                points.Add(point);
                indexByPoint.Add(point, index);
            }
            return index;
        }

        internal StepModelData Build() => new(
            points,
            [],
            triangles,
            Enumerable.Range(0, points.Count).ToArray(),
            TriangleFaceIndices: null,
            Edges: null,
            VertexPointIndices: null,
            Geometry: new MeshStepGeometry(points, triangles),
            FaceCount: 0);
    }
}
