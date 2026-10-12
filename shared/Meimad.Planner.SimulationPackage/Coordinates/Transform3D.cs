using System.Globalization;

namespace Meimad.Planner.SimulationPackage.Coordinates;

/// <summary>A point or direction in millimetres (or degrees, for an Euler triple).</summary>
public readonly record struct Vector3d(double X, double Y, double Z)
{
    public static readonly Vector3d Zero = new(0, 0, 0);

    public static Vector3d operator +(Vector3d a, Vector3d b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vector3d operator -(Vector3d a, Vector3d b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vector3d operator *(Vector3d a, double s) => new(a.X * s, a.Y * s, a.Z * s);

    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"({X:0.######}, {Y:0.######}, {Z:0.######})");
}

/// <summary>An axis-aligned bounding box in millimetres.</summary>
public readonly record struct BoundingBox(Vector3d Min, Vector3d Max)
{
    public Vector3d Size => Max - Min;

    public static BoundingBox? Of(IEnumerable<Vector3d> points)
    {
        var any = false;
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        foreach (var p in points)
        {
            any = true;
            minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y); minZ = Math.Min(minZ, p.Z);
            maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y); maxZ = Math.Max(maxZ, p.Z);
        }

        return any ? new BoundingBox(new(minX, minY, minZ), new(maxX, maxY, maxZ)) : null;
    }

    /// <summary>The eight corners (for transforming a box into another frame).</summary>
    public IEnumerable<Vector3d> Corners()
    {
        foreach (var x in new[] { Min.X, Max.X })
        foreach (var y in new[] { Min.Y, Max.Y })
        foreach (var z in new[] { Min.Z, Max.Z })
            yield return new(x, y, z);
    }

    public BoundingBox Inflate(double margin) =>
        new(Min - new Vector3d(margin, margin, margin), Max + new Vector3d(margin, margin, margin));

    public bool Intersects(BoundingBox other) =>
        Min.X <= other.Max.X && Max.X >= other.Min.X
        && Min.Y <= other.Max.Y && Max.Y >= other.Min.Y
        && Min.Z <= other.Max.Z && Max.Z >= other.Min.Z;

    public bool Contains(BoundingBox other, double tolerance = 0) =>
        other.Min.X >= Min.X - tolerance && other.Min.Y >= Min.Y - tolerance && other.Min.Z >= Min.Z - tolerance
        && other.Max.X <= Max.X + tolerance && other.Max.Y <= Max.Y + tolerance && other.Max.Z <= Max.Z + tolerance;

    public BoundingBox Transform(Transform3D transform) => Of(Corners().Select(transform.Apply))!.Value;
}

/// <summary>
/// A rigid transformation p' = R·p + t (rotation then translation), millimetres. Composition is
/// explicit: <c>a.Then(b)</c> applies <c>a</c> first and <c>b</c> second.
/// </summary>
public sealed class Transform3D
{
    public const double RotationTolerance = 1e-6;
    public const double TranslationToleranceMm = 1e-3;

    private readonly double[] r; // row-major 3×3

    public static readonly Transform3D Identity = new([1, 0, 0, 0, 1, 0, 0, 0, 1], Vector3d.Zero);

    private Transform3D(double[] rotation, Vector3d translation)
    {
        r = rotation;
        Translation = translation;
    }

    public Vector3d Translation { get; }

    /// <summary>The rotation, row-major.</summary>
    public IReadOnlyList<double> RotationMatrix => r;

    public double this[int row, int column] => r[row * 3 + column];

    public static Transform3D FromRotationTranslation(IReadOnlyList<double> rowMajorRotation, Vector3d translation)
    {
        if (rowMajorRotation.Count != 9) throw new ArgumentException("A 3×3 rotation has nine values.", nameof(rowMajorRotation));
        return new Transform3D(rowMajorRotation.ToArray(), translation);
    }

    public static Transform3D FromTranslation(Vector3d translation) => new([1, 0, 0, 0, 1, 0, 0, 0, 1], translation);

    /// <summary>Reads a row-major 4×4 homogeneous matrix (16 values, last row 0 0 0 1).</summary>
    public static Transform3D FromMatrix4(IReadOnlyList<double> m)
    {
        if (m.Count != 16) throw new ArgumentException("A 4×4 matrix has sixteen values.", nameof(m));
        return new Transform3D([m[0], m[1], m[2], m[4], m[5], m[6], m[8], m[9], m[10]], new(m[3], m[7], m[11]));
    }

    /// <summary>The row-major 4×4 homogeneous matrix.</summary>
    public double[] ToMatrix4() =>
    [
        r[0], r[1], r[2], Translation.X,
        r[3], r[4], r[5], Translation.Y,
        r[6], r[7], r[8], Translation.Z,
        0, 0, 0, 1
    ];

    /// <summary>Elementary rotation about X, Y or Z by <paramref name="degrees"/> (right-handed).</summary>
    public static Transform3D Rotation(char axis, double degrees)
    {
        var radians = degrees * Math.PI / 180.0;
        var c = Clean(Math.Cos(radians));
        var s = Clean(Math.Sin(radians));
        double[] m = char.ToUpperInvariant(axis) switch
        {
            'X' => [1, 0, 0, 0, c, -s, 0, s, c],
            'Y' => [c, 0, s, 0, 1, 0, -s, 0, c],
            'Z' => [c, -s, 0, s, c, 0, 0, 0, 1],
            _ => throw new ArgumentOutOfRangeException(nameof(axis))
        };
        return new Transform3D(m, Vector3d.Zero);
    }

    /// <summary>
    /// The rotation of an Euler triple in an explicit order. The order string is the matrix product read
    /// left to right: <c>RzRyRx</c> means R = Rz(z)·Ry(y)·Rx(x), i.e. rotate about the fixed X axis first,
    /// then Y, then Z. This is the convention of the Operation Work CS (schema v113).
    /// </summary>
    public static Transform3D FromEuler(Vector3d degrees, string rotationOrder)
    {
        var axes = RotationOrders.Parse(rotationOrder);
        var result = Identity;
        // Product M1·M2·M3 applied to p: M3 first. Then(...) applies left operand first.
        for (var index = axes.Length - 1; index >= 0; index--)
        {
            var axis = axes[index];
            var angle = axis switch { 'X' => degrees.X, 'Y' => degrees.Y, _ => degrees.Z };
            result = result.Then(Rotation(axis, angle));
        }

        return result;
    }

    /// <summary>
    /// Decomposes the rotation into the RzRyRx Euler triple (degrees) used by the stored Operation
    /// Work CS; y is in [-90, 90], x and z in (-180, 180].
    /// </summary>
    public Vector3d ToEulerRzRyRx()
    {
        // R = Rz·Ry·Rx: r20 = -sin(y), r21 = cos(y) sin(x), r22 = cos(y) cos(x), r10 = sin(z) cos(y), r00 = cos(z) cos(y).
        var sy = Math.Clamp(-r[6], -1.0, 1.0);
        var y = Math.Asin(sy);
        double x, z;
        if (Math.Abs(sy) < 1 - 1e-12)
        {
            x = Math.Atan2(r[7], r[8]);
            z = Math.Atan2(r[3], r[0]);
        }
        else
        {
            // Gimbal lock: only x - z (or x + z) is defined; keep z = 0.
            z = 0;
            x = sy > 0 ? Math.Atan2(r[1], r[4]) : Math.Atan2(-r[1], r[4]);
        }

        return new(Degrees(x), Degrees(y), Degrees(z));
    }

    public Vector3d Apply(Vector3d p) => new(
        r[0] * p.X + r[1] * p.Y + r[2] * p.Z + Translation.X,
        r[3] * p.X + r[4] * p.Y + r[5] * p.Z + Translation.Y,
        r[6] * p.X + r[7] * p.Y + r[8] * p.Z + Translation.Z);

    public Vector3d ApplyToDirection(Vector3d d) => new(
        r[0] * d.X + r[1] * d.Y + r[2] * d.Z,
        r[3] * d.X + r[4] * d.Y + r[5] * d.Z,
        r[6] * d.X + r[7] * d.Y + r[8] * d.Z);

    /// <summary>This transformation followed by <paramref name="next"/>: p → next(this(p)).</summary>
    public Transform3D Then(Transform3D next)
    {
        var a = next.r;
        var b = r;
        var m = new double[9];
        for (var i = 0; i < 3; i++)
        for (var j = 0; j < 3; j++)
            m[i * 3 + j] = Clean(a[i * 3] * b[j] + a[i * 3 + 1] * b[3 + j] + a[i * 3 + 2] * b[6 + j]);
        return new Transform3D(m, next.Apply(Translation));
    }

    /// <summary>The inverse of a rigid transformation: p = Rᵀ(p' − t).</summary>
    public Transform3D Inverse()
    {
        double[] t = [r[0], r[3], r[6], r[1], r[4], r[7], r[2], r[5], r[8]];
        var inverse = new Transform3D(t, Vector3d.Zero);
        var shifted = inverse.ApplyToDirection(Translation);
        return new Transform3D(t, new(-shifted.X, -shifted.Y, -shifted.Z));
    }

    /// <summary>True when the 3×3 part is orthonormal with determinant +1 (a proper rotation).</summary>
    public bool IsProperRotation(double tolerance = RotationTolerance)
    {
        for (var i = 0; i < 3; i++)
        for (var j = 0; j < 3; j++)
        {
            var dot = r[i] * r[j] + r[3 + i] * r[3 + j] + r[6 + i] * r[6 + j];
            if (Math.Abs(dot - (i == j ? 1 : 0)) > tolerance) return false;
        }

        return Math.Abs(Determinant() - 1) <= tolerance;
    }

    public double Determinant() =>
        r[0] * (r[4] * r[8] - r[5] * r[7]) - r[1] * (r[3] * r[8] - r[5] * r[6]) + r[2] * (r[3] * r[7] - r[4] * r[6]);

    /// <summary>Largest rotation-element difference and translation distance to <paramref name="other"/>.</summary>
    public (double Rotation, double TranslationMm) DifferenceTo(Transform3D other) =>
        (r.Zip(other.r).Max(pair => Math.Abs(pair.First - pair.Second)), (Translation - other.Translation).Length);

    public bool ApproximatelyEquals(Transform3D other, double rotationTolerance = RotationTolerance, double translationToleranceMm = TranslationToleranceMm)
    {
        var (rotation, translation) = DifferenceTo(other);
        return rotation <= rotationTolerance && translation <= translationToleranceMm;
    }

    public bool IsIdentity(double tolerance = 1e-9) => ApproximatelyEquals(Identity, tolerance, tolerance);

    private static double Degrees(double radians)
    {
        var value = radians * 180.0 / Math.PI;
        value = Math.Round(value, 9);
        if (value <= -180) value += 360;
        return value == 0 ? 0 : value; // no negative zero
    }

    // cos(90°) is 6e-17, not 0; golden tests compare exact matrices, so snap values that are zero or ±1 to the noise.
    private static double Clean(double value)
    {
        if (Math.Abs(value) < 1e-15) return 0;
        if (Math.Abs(value - 1) < 1e-15) return 1;
        if (Math.Abs(value + 1) < 1e-15) return -1;
        return value;
    }
}

/// <summary>The explicit Euler rotation orders the package format accepts.</summary>
public static class RotationOrders
{
    public const string RzRyRx = "RzRyRx";

    public static readonly IReadOnlyList<string> All = ["RzRyRx", "RzRxRy", "RyRxRz", "RyRzRx", "RxRyRz", "RxRzRy"];

    public static bool IsValid(string? order) => order is not null && All.Contains(order, StringComparer.Ordinal);

    /// <summary>The axes of the matrix product left to right, e.g. RzRyRx → Z, Y, X.</summary>
    public static char[] Parse(string order)
    {
        if (!IsValid(order)) throw new ArgumentException($"Unknown rotation order '{order}'.", nameof(order));
        return [char.ToUpperInvariant(order[1]), char.ToUpperInvariant(order[3]), char.ToUpperInvariant(order[5])];
    }
}
