using System.Buffers.Binary;
using System.IO;
using System.Text.Json;

namespace Meimad.Planner.Client.Windows.Presentation.NcViewer;

/// <summary>
/// Reads the triangles of a glTF 2 binary (.glb) Machine mesh into binary STL for the NC viewer and the Machine
/// Designer, which draw STL meshes with the color and opacity of the machine definition (glTF materials are not used).
/// Every mesh of the default scene is placed with its node transforms (matrix or translation/rotation/scale); glTF
/// lengths are metres, so positions are converted to millimetres. Compressed meshes (Draco, meshopt) and non-triangle
/// primitives are refused with a message instead of being drawn wrongly.
/// </summary>
internal static class GlbMeshReader
{
    private const uint Magic = 0x46546C67;
    private const uint JsonChunk = 0x4E4F534A;
    private const uint BinaryChunk = 0x004E4942;
    private const int MaximumTriangles = 4_000_000;
    internal const double MetresToMillimetres = 1000;

    internal static byte[] ReadAsStl(byte[] glb)
    {
        var triangles = ReadTriangles(glb);
        var count = triangles.Count / 9;
        var bytes = new byte[84 + count * 50];
        "Meimad Planner GLB mesh"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(80), (uint)count);
        var offset = 84;
        for (var triangle = 0; triangle < count; triangle++)
        {
            offset += 12;
            for (var value = 0; value < 9; value++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset), triangles[triangle * 9 + value]);
                offset += 4;
            }
            offset += 2;
        }
        return bytes;
    }

    /// <summary>Triangle corner positions in millimetres, nine floats per triangle.</summary>
    internal static List<float> ReadTriangles(byte[] glb)
    {
        if (glb.Length < 20 || BinaryPrimitives.ReadUInt32LittleEndian(glb) != Magic)
            throw new InvalidDataException("The file is not a glTF binary (.glb).");
        if (BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(4)) != 2)
            throw new InvalidDataException("Only glTF 2 binary files are supported.");
        var length = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(8)), (uint)glb.Length);
        ReadOnlyMemory<byte> json = default;
        ReadOnlyMemory<byte> binary = default;
        for (var offset = 12; offset + 8 <= length;)
        {
            var chunkLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(offset));
            var chunkType = BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(offset + 4));
            if (chunkLength < 0 || offset + 8 + chunkLength > length) throw new InvalidDataException("The glTF binary is truncated.");
            var chunk = glb.AsMemory(offset + 8, chunkLength);
            if (chunkType == JsonChunk && json.IsEmpty) json = chunk;
            else if (chunkType == BinaryChunk && binary.IsEmpty) binary = chunk;
            offset += 8 + ((chunkLength + 3) & ~3);
        }
        if (json.IsEmpty) throw new InvalidDataException("The glTF binary has no JSON chunk.");

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        foreach (var required in Array(root, "extensionsRequired"))
        {
            throw new InvalidDataException($"The glTF file needs the extension {required.GetString()}; export it without compression.");
        }
        var model = new Model(root, binary);
        var triangles = new List<float>();
        var nodes = Array(root, "nodes").ToArray();
        IEnumerable<int> roots;
        var scenes = Array(root, "scenes").ToArray();
        if (scenes.Length > 0)
        {
            var sceneIndex = root.TryGetProperty("scene", out var scene) && scene.TryGetInt32(out var chosen) && chosen >= 0 && chosen < scenes.Length ? chosen : 0;
            roots = Array(scenes[sceneIndex], "nodes").Select(node => node.GetInt32());
        }
        else
        {
            var children = nodes.SelectMany(node => Array(node, "children").Select(child => child.GetInt32())).ToHashSet();
            roots = Enumerable.Range(0, nodes.Length).Where(index => !children.Contains(index));
        }
        var scale = Scale(MetresToMillimetres);
        foreach (var index in roots) Visit(model, nodes, index, scale, triangles, depth: 0);
        if (triangles.Count == 0) throw new InvalidDataException("The glTF file has no triangle meshes.");
        return triangles;
    }

    private static void Visit(Model model, JsonElement[] nodes, int index, double[] parent, List<float> triangles, int depth)
    {
        if (index < 0 || index >= nodes.Length || depth > 64) throw new InvalidDataException("The glTF node hierarchy is invalid.");
        var node = nodes[index];
        var world = Multiply(parent, LocalMatrix(node));
        if (node.TryGetProperty("mesh", out var mesh) && mesh.TryGetInt32(out var meshIndex))
            AddMesh(model, meshIndex, world, triangles);
        foreach (var child in Array(node, "children")) Visit(model, nodes, child.GetInt32(), world, triangles, depth + 1);
    }

    private static void AddMesh(Model model, int meshIndex, double[] world, List<float> triangles)
    {
        var meshes = Array(model.Root, "meshes").ToArray();
        if (meshIndex < 0 || meshIndex >= meshes.Length) throw new InvalidDataException("A glTF node refers to a missing mesh.");
        foreach (var primitive in Array(meshes[meshIndex], "primitives"))
        {
            var mode = primitive.TryGetProperty("mode", out var modeValue) ? modeValue.GetInt32() : 4;
            if (mode != 4) continue; // points and lines have no surface
            if (!primitive.TryGetProperty("attributes", out var attributes) || !attributes.TryGetProperty("POSITION", out var position))
                continue;
            var points = model.Floats(position.GetInt32(), 3);
            var indices = primitive.TryGetProperty("indices", out var indexAccessor)
                ? model.Indices(indexAccessor.GetInt32())
                : Enumerable.Range(0, points.Length / 3).ToArray();
            if (triangles.Count / 9 + indices.Length / 3 > MaximumTriangles) throw new InvalidDataException("The glTF file has too many triangles to show.");
            for (var corner = 0; corner + 2 < indices.Length; corner += 3)
            {
                for (var k = 0; k < 3; k++)
                {
                    var i = indices[corner + k] * 3;
                    if (i < 0 || i + 2 >= points.Length) throw new InvalidDataException("A glTF index is outside its positions.");
                    var (x, y, z) = (points[i], points[i + 1], points[i + 2]);
                    triangles.Add((float)(world[0] * x + world[1] * y + world[2] * z + world[3]));
                    triangles.Add((float)(world[4] * x + world[5] * y + world[6] * z + world[7]));
                    triangles.Add((float)(world[8] * x + world[9] * y + world[10] * z + world[11]));
                }
            }
        }
    }

    /// <summary>A node's transform as a row-major matrix: its column-major <c>matrix</c>, else T·R·S.</summary>
    private static double[] LocalMatrix(JsonElement node)
    {
        if (node.TryGetProperty("matrix", out var matrix) && matrix.GetArrayLength() == 16)
        {
            var m = matrix.EnumerateArray().Select(value => value.GetDouble()).ToArray();
            return [m[0], m[4], m[8], m[12], m[1], m[5], m[9], m[13], m[2], m[6], m[10], m[14], m[3], m[7], m[11], m[15]];
        }
        var t = Vector(node, "translation", [0, 0, 0]);
        var r = Vector(node, "rotation", [0, 0, 0, 1]);
        var s = Vector(node, "scale", [1, 1, 1]);
        var (x, y, z, w) = (r[0], r[1], r[2], r[3]);
        double[] rotation =
        [
            1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w),
            2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w),
            2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)
        ];
        return
        [
            rotation[0] * s[0], rotation[1] * s[1], rotation[2] * s[2], t[0],
            rotation[3] * s[0], rotation[4] * s[1], rotation[5] * s[2], t[1],
            rotation[6] * s[0], rotation[7] * s[1], rotation[8] * s[2], t[2],
            0, 0, 0, 1
        ];
    }

    private static double[] Vector(JsonElement node, string name, double[] fallback) =>
        node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == fallback.Length
            ? value.EnumerateArray().Select(item => item.GetDouble()).ToArray()
            : fallback;

    private static double[] Scale(double factor) => [factor, 0, 0, 0, 0, factor, 0, 0, 0, 0, factor, 0, 0, 0, 0, 1];

    private static double[] Multiply(double[] a, double[] b)
    {
        var result = new double[16];
        for (var row = 0; row < 4; row++)
            for (var column = 0; column < 4; column++)
                for (var k = 0; k < 4; k++)
                    result[row * 4 + column] += a[row * 4 + k] * b[k * 4 + column];
        return result;
    }

    private static IEnumerable<JsonElement> Array(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
            : [];

    private sealed class Model(JsonElement root, ReadOnlyMemory<byte> binary)
    {
        internal JsonElement Root { get; } = root;
        private readonly JsonElement[] accessors = Array(root, "accessors").ToArray();
        private readonly JsonElement[] views = Array(root, "bufferViews").ToArray();

        internal float[] Floats(int accessorIndex, int components)
        {
            var (accessor, data, stride, count) = Locate(accessorIndex);
            if (accessor.GetProperty("componentType").GetInt32() != 5126)
                throw new InvalidDataException("glTF positions must be 32-bit floats.");
            var values = new float[count * components];
            var step = stride == 0 ? components * 4 : stride;
            if (count > 0 && (long)(count - 1) * step + components * 4 > data.Length)
                throw new InvalidDataException("A glTF accessor is longer than its buffer view.");
            for (var item = 0; item < count; item++)
                for (var component = 0; component < components; component++)
                    values[item * components + component] = BinaryPrimitives.ReadSingleLittleEndian(data.Span[(item * step + component * 4)..]);
            return values;
        }

        internal int[] Indices(int accessorIndex)
        {
            var (accessor, data, stride, count) = Locate(accessorIndex);
            var type = accessor.GetProperty("componentType").GetInt32();
            var size = type switch { 5121 => 1, 5123 => 2, 5125 => 4, _ => throw new InvalidDataException("glTF indices must be unsigned integers.") };
            var step = stride == 0 ? size : stride;
            if (count > 0 && (long)(count - 1) * step + size > data.Length)
                throw new InvalidDataException("A glTF accessor is longer than its buffer view.");
            var values = new int[count];
            for (var item = 0; item < count; item++)
            {
                var span = data.Span[(item * step)..];
                values[item] = size switch
                {
                    1 => span[0],
                    2 => BinaryPrimitives.ReadUInt16LittleEndian(span),
                    _ => checked((int)BinaryPrimitives.ReadUInt32LittleEndian(span))
                };
            }
            return values;
        }

        private (JsonElement Accessor, ReadOnlyMemory<byte> Data, int Stride, int Count) Locate(int accessorIndex)
        {
            if (accessorIndex < 0 || accessorIndex >= accessors.Length) throw new InvalidDataException("A glTF accessor is missing.");
            var accessor = accessors[accessorIndex];
            if (accessor.TryGetProperty("sparse", out _)) throw new InvalidDataException("Sparse glTF accessors are not supported.");
            var count = accessor.GetProperty("count").GetInt32();
            if (!accessor.TryGetProperty("bufferView", out var viewIndex)) throw new InvalidDataException("A glTF accessor has no data.");
            var view = views[viewIndex.GetInt32()];
            if (view.TryGetProperty("buffer", out var buffer) && buffer.GetInt32() != 0)
                throw new InvalidDataException("Only the embedded glTF binary buffer is supported.");
            var start = (view.TryGetProperty("byteOffset", out var viewOffset) ? viewOffset.GetInt32() : 0)
                + (accessor.TryGetProperty("byteOffset", out var accessorOffset) ? accessorOffset.GetInt32() : 0);
            var viewLength = view.GetProperty("byteLength").GetInt32();
            var stride = view.TryGetProperty("byteStride", out var strideValue) ? strideValue.GetInt32() : 0;
            var end = (view.TryGetProperty("byteOffset", out var offsetAgain) ? offsetAgain.GetInt32() : 0) + viewLength;
            if (start < 0 || end > binary.Length || start > end) throw new InvalidDataException("A glTF buffer view is outside the binary chunk.");
            return (accessor, binary[start..end], stride, count);
        }
    }
}
