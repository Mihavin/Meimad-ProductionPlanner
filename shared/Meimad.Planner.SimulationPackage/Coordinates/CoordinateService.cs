using Meimad.Planner.SimulationPackage.Diagnostics;
using Meimad.Planner.SimulationPackage.Manifest;

namespace Meimad.Planner.SimulationPackage.Coordinates;

/// <summary>The program side of a coordinate resolution: the package coordinate system and file units.</summary>
public sealed record CoordinateProgram(CoordinateSystemDefinition? CoordinateSystem, string? Units);

/// <summary>
/// The Operation side: the Work CS stored for the Case Operation (schema v113, origin + RzRyRx rotation
/// of the UCS expressed in part-model coordinates). When it is given it wins over the package (the
/// planner may correct it after import); otherwise the package coordinate system is used.
/// </summary>
public sealed record CoordinateOperation(Vector3d? WorkCsOriginMm = null, Vector3d? WorkCsRotationDeg = null, string? WorkOffset = null);

/// <summary>
/// The Machine side: a measured work offset (G54/G55… from DPRNT or manual entry) in machine
/// coordinates. It is never part of the package; it comes from the measured-offset store. Rotations are
/// applied as RzRyRx about the table axes, which is exact for 3-axis setups; rotary kinematics belong to
/// the simulation engine.
/// </summary>
public sealed record CoordinateMachine(string? MachineId, string? WorkOffset, Vector3d PositionMm, Vector3d RotationDeg);

/// <summary>
/// The resolved chain <c>file coordinates → Cimatron UCS → NC zero → machine coordinates</c>. Every
/// stage is explicit. Points are first scaled from file units into millimetres
/// (<see cref="FileUnitScale"/>), then mapped by <see cref="FileToUcs"/>, then <see cref="UcsToNcZero"/>,
/// then (only when a measured offset is known) <see cref="NcZeroToMachine"/>.
/// </summary>
public sealed record CoordinateContext(
    string Mode,
    string? UcsName,
    string? WorkOffset,
    double FileUnitScale,
    Transform3D FileToUcs,
    Transform3D UcsToNcZero,
    Transform3D? NcZeroToMachine,
    IReadOnlyList<PackageDiagnostic> Diagnostics)
{
    public bool IsResolved => !Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    /// <summary>File point → NC work coordinates (millimetres).</summary>
    public Vector3d FileToNc(Vector3d filePoint) => UcsToNcZero.Apply(FileToUcs.Apply(filePoint * FileUnitScale));

    /// <summary>File point → machine coordinates; null when no measured work offset is known.</summary>
    public Vector3d? FileToMachine(Vector3d filePoint) =>
        NcZeroToMachine is null ? null : NcZeroToMachine.Apply(FileToNc(filePoint));

    /// <summary>The combined file → NC work transformation (after unit scaling).</summary>
    public Transform3D FileToNcTransform => FileToUcs.Then(UcsToNcZero);
}

public interface ICoordinateService
{
    /// <summary>Resolves the coordinate chain of one program for one Operation on one Machine.</summary>
    CoordinateContext Resolve(CoordinateProgram program, CoordinateOperation? operation, CoordinateMachine? machine);
}

/// <summary>
/// Implements the coordinate policy of the Cimatron exporter definition §5–§6. Mode A
/// (<c>nc-procedure-ucs</c>): files are already in NC work coordinates, identity. Mode B
/// (<c>part-model-coordinates</c>): the UCS frame is given in model coordinates as p_model = R·p_ucs + O,
/// so a file point maps into the UCS as Rᵀ(p − O). When both a matrix and origin/rotation are present
/// they must agree. Cimatron posts NC relative to the procedure UCS, so UCS → NC zero is identity.
/// Measured machine offsets only enter through <see cref="CoordinateMachine"/>; an unknown coordinate
/// system is never guessed.
/// </summary>
public sealed class CoordinateService : ICoordinateService
{
    public CoordinateContext Resolve(CoordinateProgram program, CoordinateOperation? operation, CoordinateMachine? machine)
    {
        ArgumentNullException.ThrowIfNull(program);
        var diagnostics = new List<PackageDiagnostic>();
        var scale = PackageUnits.ToMillimetres(program.Units);
        if (scale is null)
        {
            diagnostics.Add(PackageDiagnostic.Error(PackageDiagnosticCodes.UnitsUnknown,
                "The package units are unknown; only mm and inch are accepted.", ("units", program.Units)));
        }

        var definition = program.CoordinateSystem;
        Transform3D fileToUcs = Transform3D.Identity;
        string mode = definition?.Type ?? "unknown";
        if (operation?.WorkCsOriginMm is not null || operation?.WorkCsRotationDeg is not null)
        {
            // The stored Operation Work CS (planner may have corrected the imported one).
            var ucsInModel = Transform3D.FromEuler(operation.WorkCsRotationDeg ?? Vector3d.Zero, RotationOrders.RzRyRx)
                .Then(Transform3D.FromTranslation(operation.WorkCsOriginMm ?? Vector3d.Zero));
            fileToUcs = ucsInModel.Inverse();
            mode = "operation-work-cs";
        }
        else
        {
            var resolved = ResolveUcsInModel(definition, diagnostics);
            if (resolved is not null) fileToUcs = resolved.Inverse();
        }

        Transform3D? ncToMachine = null;
        if (machine is not null)
        {
            ncToMachine = Transform3D.FromEuler(machine.RotationDeg, RotationOrders.RzRyRx)
                .Then(Transform3D.FromTranslation(machine.PositionMm));
            var expected = operation?.WorkOffset ?? definition?.WorkOffset;
            if (!string.IsNullOrWhiteSpace(expected) && !string.IsNullOrWhiteSpace(machine.WorkOffset)
                && !string.Equals(Normalize(expected), Normalize(machine.WorkOffset), StringComparison.Ordinal))
            {
                diagnostics.Add(PackageDiagnostic.Warning(PackageDiagnosticCodes.WorkOffsetMismatch,
                    "The measured work offset is not the work offset the program uses.",
                    ("programWorkOffset", expected), ("measuredWorkOffset", machine.WorkOffset)));
            }
        }

        return new CoordinateContext(
            mode,
            definition?.Name,
            operation?.WorkOffset ?? definition?.WorkOffset,
            scale ?? 1.0,
            fileToUcs,
            Transform3D.Identity,
            ncToMachine,
            diagnostics);
    }

    /// <summary>
    /// The UCS frame in file (model) coordinates: identity for mode A, R/O for mode B. Null (with a
    /// blocking diagnostic) when the definition is missing, unknown or inconsistent.
    /// </summary>
    public static Transform3D? ResolveUcsInModel(CoordinateSystemDefinition? definition, ICollection<PackageDiagnostic> diagnostics)
    {
        if (definition is null || string.IsNullOrWhiteSpace(definition.Name))
        {
            diagnostics.Add(PackageDiagnostic.Error(PackageDiagnosticCodes.UcsMissing,
                "The package does not name the Cimatron procedure UCS; geometry cannot be placed safely."));
            return null;
        }

        switch (definition.Type)
        {
            case CoordinateSystemTypes.NcProcedureUcs:
            {
                var conflicts = new List<string>();
                if (definition.Transform is not null && definition.Transform != CoordinateSystemTypes.IdentityTransform)
                    conflicts.Add("transform");
                if (definition.FilesAreIn is not null && definition.FilesAreIn != CoordinateSystemTypes.FilesInNcWorkCoordinates)
                    conflicts.Add("filesAreIn");
                if (definition.OriginMm is { } origin && new Vector3d(origin.X, origin.Y, origin.Z).Length > Transform3D.TranslationToleranceMm)
                    conflicts.Add("originMm");
                if (definition.RotationDeg is { } rotation && new Vector3d(rotation.X, rotation.Y, rotation.Z).Length > 1e-6)
                    conflicts.Add("rotationDeg");
                if (definition.Matrix is { Count: 16 } matrix && !Transform3D.FromMatrix4(matrix).IsIdentity(1e-6))
                    conflicts.Add("matrix");
                if (conflicts.Count > 0)
                {
                    diagnostics.Add(PackageDiagnostic.Error(PackageDiagnosticCodes.CoordinateModeConflict,
                        "Mode nc-procedure-ucs means the files are already in NC work coordinates, but a non-identity transformation is given.",
                        ("ucs", definition.Name), ("fields", string.Join(",", conflicts))));
                    return null;
                }

                return Transform3D.Identity;
            }
            case CoordinateSystemTypes.PartModelCoordinates:
                return ResolvePartModel(definition, diagnostics);
            default:
                diagnostics.Add(PackageDiagnostic.Error(PackageDiagnosticCodes.UcsTypeUnknown,
                    "The coordinate system type is unknown; geometry cannot be placed safely.",
                    ("ucs", definition.Name), ("type", definition.Type)));
                return null;
        }
    }

    /// <summary>
    /// The Operation Work CS (schema v113: origin + RzRyRx rotation of the UCS in part-model coordinates)
    /// that reproduces the package coordinate system: identity for mode A, the decomposed UCS frame for
    /// mode B. Null when the definition does not resolve.
    /// </summary>
    public static (Vector3d OriginMm, Vector3d RotationDeg)? ToOperationWorkCs(CoordinateSystemDefinition? definition)
    {
        var ucsInModel = ResolveUcsInModel(definition, new List<PackageDiagnostic>());
        if (ucsInModel is null) return null;
        var origin = ucsInModel.Translation;
        return (new Vector3d(Round(origin.X), Round(origin.Y), Round(origin.Z)), ucsInModel.ToEulerRzRyRx());
    }

    private static double Round(double value)
    {
        var rounded = Math.Round(value, 6);
        return rounded == 0 ? 0 : rounded;
    }

    private static Transform3D? ResolvePartModel(CoordinateSystemDefinition definition, ICollection<PackageDiagnostic> diagnostics)
    {
        if (definition.FilesAreIn is not null && definition.FilesAreIn != CoordinateSystemTypes.FilesInPartModelCoordinates)
        {
            diagnostics.Add(PackageDiagnostic.Error(PackageDiagnosticCodes.CoordinateModeConflict,
                "Mode part-model-coordinates requires filesAreIn = part-model-coordinates.",
                ("ucs", definition.Name), ("filesAreIn", definition.FilesAreIn)));
            return null;
        }

        Transform3D? fromEuler = null;
        if (definition.OriginMm is not null || definition.RotationDeg is not null || definition.RotationOrder is not null)
        {
            if (definition.OriginMm is null || definition.RotationDeg is null || !RotationOrders.IsValid(definition.RotationOrder))
            {
                diagnostics.Add(PackageDiagnostic.Error(PackageDiagnosticCodes.UcsTransformIncomplete,
                    "Mode part-model-coordinates needs originMm, rotationDeg and an explicit rotationOrder together.",
                    ("ucs", definition.Name), ("rotationOrder", definition.RotationOrder)));
                return null;
            }

            var rotation = definition.RotationDeg;
            var origin = definition.OriginMm;
            fromEuler = Transform3D.FromEuler(new(rotation.X, rotation.Y, rotation.Z), definition.RotationOrder!)
                .Then(Transform3D.FromTranslation(new(origin.X, origin.Y, origin.Z)));
        }

        Transform3D? fromMatrix = null;
        if (definition.Matrix is not null)
        {
            var m = definition.Matrix;
            if (m.Count != 16 || m.Any(value => !double.IsFinite(value))
                || Math.Abs(m[12]) > 1e-9 || Math.Abs(m[13]) > 1e-9 || Math.Abs(m[14]) > 1e-9 || Math.Abs(m[15] - 1) > 1e-9)
            {
                diagnostics.Add(PackageDiagnostic.Error(PackageDiagnosticCodes.UcsMatrixInvalid,
                    "The UCS matrix must be a row-major 4×4 homogeneous matrix with last row 0 0 0 1.", ("ucs", definition.Name)));
                return null;
            }

            fromMatrix = Transform3D.FromMatrix4(m);
            if (!fromMatrix.IsProperRotation())
            {
                diagnostics.Add(PackageDiagnostic.Error(PackageDiagnosticCodes.UcsMatrixInvalid,
                    "The UCS matrix rotation is not orthonormal with determinant +1 (scaled, sheared or mirrored).",
                    ("ucs", definition.Name), ("determinant", fromMatrix.Determinant().ToString("0.######", System.Globalization.CultureInfo.InvariantCulture))));
                return null;
            }
        }

        if (fromEuler is null && fromMatrix is null)
        {
            diagnostics.Add(PackageDiagnostic.Error(PackageDiagnosticCodes.UcsTransformIncomplete,
                "Mode part-model-coordinates needs originMm + rotationDeg + rotationOrder or a matrix.", ("ucs", definition.Name)));
            return null;
        }

        if (fromEuler is not null && fromMatrix is not null && !fromEuler.ApproximatelyEquals(fromMatrix))
        {
            var (rotationDifference, translationDifference) = fromEuler.DifferenceTo(fromMatrix);
            diagnostics.Add(PackageDiagnostic.Error(PackageDiagnosticCodes.UcsMatrixInconsistent,
                "The UCS matrix and the origin/rotation values describe different coordinate systems.",
                ("ucs", definition.Name),
                ("rotationDifference", rotationDifference.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)),
                ("translationDifferenceMm", translationDifference.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture))));
            return null;
        }

        return fromMatrix ?? fromEuler;
    }

    private static string Normalize(string value) => value.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
}
