namespace Meimad.Planner.NcEngine.MachineDefinitions;

/// <summary>
/// Forward kinematics of a <see cref="MachineSimulationDefinition"/> (row-major 4x4 matrices, as CncKinematics uses):
/// linear <c>T(position) · T(scale·q·d)</c>, rotary <c>T(position) · T(p) · R(d, scale·q) · T(-p)</c> with
/// <c>p = point - position</c>; ancestors compose root first and q = 0 gives the authored zero pose. The NC engine
/// evaluates the compiled definition with the same formula; tests keep the two equal.
/// </summary>
public static class MachineKinematics
{
    public static double[] Identity() => [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];

    public static double[] Translation(double x, double y, double z) => [1, 0, 0, x, 0, 1, 0, y, 0, 0, 1, z, 0, 0, 0, 1];

    public static double[] Multiply(double[] a, double[] b)
    {
        var result = new double[16];
        for (var row = 0; row < 4; row++)
            for (var column = 0; column < 4; column++)
            {
                double sum = 0;
                for (var k = 0; k < 4; k++) sum += a[row * 4 + k] * b[k * 4 + column];
                result[row * 4 + column] = sum;
            }
        return result;
    }

    /// <summary>Right-hand rotation about the unit vector <paramref name="axis"/> by <paramref name="degrees"/>.</summary>
    public static double[] Rotation(double[] axis, double degrees)
    {
        MachineVectors.TryNormalize(axis, out var d);
        var radians = degrees * Math.PI / 180;
        var c = Math.Cos(radians);
        var s = Math.Sin(radians);
        var t = 1 - c;
        var (x, y, z) = (d[0], d[1], d[2]);
        return
        [
            t * x * x + c, t * x * y - s * z, t * x * z + s * y, 0,
            t * x * y + s * z, t * y * y + c, t * y * z - s * x, 0,
            t * x * z - s * y, t * y * z + s * x, t * z * z + c, 0,
            0, 0, 0, 1
        ];
    }

    /// <summary>Degrees about X, then Y, then Z (fixed axes): R = Rz·Ry·Rx.</summary>
    public static double[] EulerXyz(double[] degrees) =>
        Multiply(Rotation([0, 0, 1], degrees[2]), Multiply(Rotation([0, 1, 0], degrees[1]), Rotation([1, 0, 0], degrees[0])));

    public static double[] TransformPoint(double[] m, double[] p) =>
    [
        m[0] * p[0] + m[1] * p[1] + m[2] * p[2] + m[3],
        m[4] * p[0] + m[5] * p[1] + m[6] * p[2] + m[7],
        m[8] * p[0] + m[9] * p[1] + m[10] * p[2] + m[11]
    ];

    /// <summary>The node's transform in its parent's frame at the reading <paramref name="value"/>.</summary>
    public static double[] Local(MachineNode node, double value)
    {
        var position = MachineVectors.IsFinite3(node.Position) ? node.Position : [0, 0, 0];
        var local = Translation(position[0], position[1], position[2]);
        if (node.Joint is not { } joint || !MachineVectors.TryNormalize(joint.Direction, out var d)) return local;
        var amount = value * (double.IsFinite(joint.Scale) && joint.Scale != 0 ? joint.Scale : 1);
        if (!joint.IsRotary) return Multiply(local, Translation(d[0] * amount, d[1] * amount, d[2] * amount));
        var point = MachineVectors.IsFinite3(joint.Point) ? joint.Point! : position;
        var pivot = MachineVectors.Subtract(point, position);
        return Multiply(local, Multiply(Translation(pivot[0], pivot[1], pivot[2]),
            Multiply(Rotation(d, amount), Translation(-pivot[0], -pivot[1], -pivot[2]))));
    }

    /// <summary>World matrices of every node at the physical axis readings (missing axes read 0).</summary>
    public static IReadOnlyDictionary<string, double[]> Evaluate(
        MachineSimulationDefinition definition, IReadOnlyDictionary<string, double>? axes = null)
    {
        var byId = definition.Nodes.GroupBy(node => node.Id).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var world = new Dictionary<string, double[]>(StringComparer.Ordinal);
        double[] Resolve(MachineNode node, int depth)
        {
            if (world.TryGetValue(node.Id, out var known)) return known;
            if (depth > byId.Count) throw new InvalidOperationException("The machine hierarchy has a cycle.");
            var parent = node.Parent is not null && byId.TryGetValue(node.Parent, out var parentNode)
                ? Resolve(parentNode, depth + 1)
                : Identity();
            var value = node.Joint is { } joint && axes is not null && axes.TryGetValue(joint.Axis, out var reading) ? reading : 0;
            var matrix = Multiply(parent, Local(node, value));
            world[node.Id] = matrix;
            return matrix;
        }
        foreach (var node in definition.Nodes) Resolve(node, 0);
        return world;
    }

    /// <summary>A mount frame (tool or workpiece) in machine coordinates at the readings.</summary>
    public static double[]? MountPosition(MachineSimulationDefinition definition, MachineMount? mount, IReadOnlyDictionary<string, double>? axes = null)
    {
        if (mount?.Node is null) return null;
        var world = Evaluate(definition, axes);
        return world.TryGetValue(mount.Node, out var matrix)
            ? TransformPoint(matrix, MachineVectors.IsFinite3(mount.Position) ? mount.Position : [0, 0, 0])
            : null;
    }
}
