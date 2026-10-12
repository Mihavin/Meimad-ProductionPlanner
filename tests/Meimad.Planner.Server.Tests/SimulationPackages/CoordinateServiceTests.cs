using System.Text.Json;
using Meimad.Planner.SimulationPackage.Coordinates;
using Meimad.Planner.SimulationPackage.Diagnostics;
using Meimad.Planner.SimulationPackage.Geometry;
using Meimad.Planner.SimulationPackage.Manifest;
using Meimad.Planner.SimulationPackage.Nc;

namespace Meimad.Planner.Server.Tests.SimulationPackages;

/// <summary>
/// Golden coordinate tests of the chain file coordinates → Cimatron UCS → NC zero → machine coordinates
/// (exporter definition §17 "Coordinate tests"; execution plan Stage 0).
/// </summary>
public sealed class CoordinateServiceTests
{
    private const double Tolerance = 1e-9;
    private readonly CoordinateService service = new();

    private static CoordinateSystemDefinition PartModel(double ox, double oy, double oz, double rx, double ry, double rz, string order = "RzRyRx", IReadOnlyList<double>? matrix = null) => new()
    {
        Type = CoordinateSystemTypes.PartModelCoordinates,
        Name = "UCS_TEST",
        FilesAreIn = CoordinateSystemTypes.FilesInPartModelCoordinates,
        OriginMm = new(ox, oy, oz),
        RotationDeg = new(rx, ry, rz),
        RotationOrder = order,
        Matrix = matrix
    };

    private static void Near(Vector3d expected, Vector3d actual, double tolerance = Tolerance)
    {
        Assert.True((expected - actual).Length <= tolerance, $"expected {expected}, got {actual}");
    }

    [Fact]
    public void Identity_ucs_keeps_file_points_in_nc_work_coordinates()
    {
        var context = service.Resolve(new(new CoordinateSystemDefinition
        {
            Type = CoordinateSystemTypes.NcProcedureUcs, Name = "UCS_OP010", WorkOffset = "G54",
            FilesAreIn = CoordinateSystemTypes.FilesInNcWorkCoordinates, Transform = "identity"
        }, "mm"), null, null);

        Assert.True(context.IsResolved);
        Assert.Equal("nc-procedure-ucs", context.Mode);
        Assert.True(context.FileToUcs.IsIdentity());
        Assert.True(context.UcsToNcZero.IsIdentity());
        Assert.Null(context.NcZeroToMachine);
        Near(new(12.5, -7, -30), context.FileToNc(new(12.5, -7, -30)));
        Assert.Null(context.FileToMachine(new(1, 2, 3)));
    }

    [Fact]
    public void Translation_only_ucs_subtracts_the_origin()
    {
        var context = service.Resolve(new(PartModel(125.4, -80, 42.7, 0, 0, 0), "mm"), null, null);
        Assert.True(context.IsResolved);
        Near(new(0, 0, 0), context.FileToNc(new(125.4, -80, 42.7)), 1e-9);
        Near(new(-125.4, 80, -42.7), context.FileToNc(new(0, 0, 0)), 1e-9);
    }

    [Theory]
    [InlineData('X', 90, 0, 1, 0, 0, 0, -1)]
    [InlineData('Y', 90, 1, 0, 0, 0, 0, 1)]
    [InlineData('Z', 90, 1, 0, 0, 0, -1, 0)]
    public void Elementary_rotations_follow_the_right_hand_rule(char axis, double degrees, double px, double py, double pz, double ex, double ey, double ez)
    {
        // The UCS axes in model coordinates are the columns of R; a model point p lies at Rᵀ p in the UCS.
        var rotation = Transform3D.Rotation(axis, degrees);
        var ucs = rotation.Inverse().Apply(new(px, py, pz));
        Near(new(ex, ey, ez), ucs);
    }

    [Fact]
    public void Elementary_rotation_matrices_are_exact()
    {
        Assert.Equal(new double[] { 1, 0, 0, 0, 0, -1, 0, 1, 0 }, Transform3D.Rotation('X', 90).RotationMatrix);
        Assert.Equal(new double[] { 0, 0, 1, 0, 1, 0, -1, 0, 0 }, Transform3D.Rotation('Y', 90).RotationMatrix);
        Assert.Equal(new double[] { 0, -1, 0, 1, 0, 0, 0, 0, 1 }, Transform3D.Rotation('Z', 90).RotationMatrix);
    }

    [Fact]
    public void RzRyRx_is_the_product_rz_ry_rx_applied_right_to_left()
    {
        var euler = Transform3D.FromEuler(new(30, 45, 60), "RzRyRx");
        var manual = Transform3D.Rotation('X', 30).Then(Transform3D.Rotation('Y', 45)).Then(Transform3D.Rotation('Z', 60));
        Assert.True(euler.ApproximatelyEquals(manual, 1e-12, 1e-12));

        // Golden: Rz(60)·Ry(45)·Rx(30) applied to the X axis.
        var c45 = Math.Sqrt(0.5);
        Near(new(0.5 * c45, Math.Sqrt(3) / 2 * c45, -c45), euler.ApplyToDirection(new(1, 0, 0)), 1e-12);

        // Another order gives another rotation.
        var other = Transform3D.FromEuler(new(30, 45, 60), "RxRyRz");
        Assert.False(euler.ApproximatelyEquals(other));
        Assert.True(other.ApproximatelyEquals(Transform3D.Rotation('Z', 60).Then(Transform3D.Rotation('Y', 45)).Then(Transform3D.Rotation('X', 30)), 1e-12, 1e-12));
    }

    [Theory]
    [InlineData(30, 45, 60)]
    [InlineData(-170, 10, 179)]
    [InlineData(0, 90, 0)]
    [InlineData(25, -90, 0)]
    [InlineData(-45, -30, -120)]
    public void Euler_decomposition_round_trips(double x, double y, double z)
    {
        var rotation = Transform3D.FromEuler(new(x, y, z), "RzRyRx");
        var angles = rotation.ToEulerRzRyRx();
        Assert.True(Transform3D.FromEuler(angles, "RzRyRx").ApproximatelyEquals(rotation, 1e-9, 1e-9), $"{angles}");
    }

    [Fact]
    public void Negative_coordinates_and_negative_angles_map_exactly()
    {
        var context = service.Resolve(new(PartModel(-50, -20, -10, 0, 0, -90), "mm"), null, null);
        // UCS X axis = Rz(-90)·X = model -Y; the model point origin + (0,-1,0)·5 is UCS (5,0,0).
        Near(new(5, 0, 0), context.FileToNc(new(-50, -25, -10)));
        Near(new(-10, -30, -5), context.FileToNc(new(-80, -10, -15)));
    }

    [Fact]
    public void Rotated_3_plus_2_golden_package_maps_stock_and_part_into_the_tilted_ucs()
    {
        using var expected = GoldenPackages.Expected();
        var golden = expected.RootElement.GetProperty("packages").GetProperty("3plus2");
        var folder = Path.Combine(GoldenPackages.Source, "3plus2");
        var manifest = ManifestJson.Deserialize(File.ReadAllText(Path.Combine(folder, "manifest.json")));
        var context = service.Resolve(new(manifest.CoordinateSystem, manifest.Units), null, null);
        Assert.True(context.IsResolved, string.Join(";", context.Diagnostics.Select(item => item.Code)));
        Assert.Equal("UCS_OP020", context.UcsName);
        Assert.Equal("G55", context.WorkOffset);

        foreach (var file in golden.GetProperty("files").EnumerateObject())
        {
            var mesh = MeshFiles.Read(Path.Combine(folder, file.Name));
            Assert.True(mesh.Readable, file.Name);
            AssertBox(file.Value.GetProperty("file"), mesh.Bounds!.Value, file.Name);
            AssertBox(file.Value.GetProperty("ucs"), mesh.Bounds.Value.Transform(context.FileToNcTransform), file.Name + " in UCS");
        }

        // The toolpath (programmed in the tilted plane) lies on the top face of the stock in the UCS.
        var nc = NcProgramScanner.ScanFile(Path.Combine(folder, "nc", "O1002.nc"));
        AssertBox(golden.GetProperty("toolpathCutting"), nc.CuttingBounds!.Value, "toolpath");
        var stock = MeshFiles.Read(Path.Combine(folder, "geometry", "stock-op020-from-op010.stl")).Bounds!.Value.Transform(context.FileToNcTransform);
        Assert.True(stock.Contains(nc.CuttingBounds.Value), "the 3+2 toolpath must lie inside the stock in UCS coordinates");

        // The stored Operation Work CS reproduces the same mapping (schema v113 R = Rz·Ry·Rx).
        var workCs = CoordinateService.ToOperationWorkCs(manifest.CoordinateSystem)!.Value;
        var origin = golden.GetProperty("workCs").GetProperty("originMm");
        var rotation = golden.GetProperty("workCs").GetProperty("rotationDeg");
        Near(new(origin[0].GetDouble(), origin[1].GetDouble(), origin[2].GetDouble()), workCs.OriginMm, 1e-6);
        Near(new(rotation[0].GetDouble(), rotation[1].GetDouble(), rotation[2].GetDouble()), workCs.RotationDeg, 1e-6);
        var fromOperation = service.Resolve(new(manifest.CoordinateSystem, "mm"), new(workCs.OriginMm, workCs.RotationDeg, "G55"), null);
        Assert.True(fromOperation.FileToUcs.ApproximatelyEquals(context.FileToUcs));
    }

    [Theory]
    [InlineData("3-axis")]
    [InlineData("5-axis")]
    public void Identity_golden_packages_keep_file_bounds_and_toolpath_touches_stock(string name)
    {
        using var expected = GoldenPackages.Expected();
        var golden = expected.RootElement.GetProperty("packages").GetProperty(name);
        var folder = Path.Combine(GoldenPackages.Source, name);
        var manifest = ManifestJson.Deserialize(File.ReadAllText(Path.Combine(folder, "manifest.json")));
        var context = service.Resolve(new(manifest.CoordinateSystem, manifest.Units), null, null);
        Assert.True(context.IsResolved);
        foreach (var file in golden.GetProperty("files").EnumerateObject())
        {
            var bounds = MeshFiles.Read(Path.Combine(folder, file.Name)).Bounds!.Value;
            AssertBox(file.Value.GetProperty("file"), bounds, file.Name);
            AssertBox(file.Value.GetProperty("ucs"), bounds.Transform(context.FileToNcTransform), file.Name);
        }

        var nc = NcProgramScanner.ScanFile(Path.Combine(folder, "nc", manifest.Source!.NcFileName!));
        AssertBox(golden.GetProperty("toolpathCutting"), nc.CuttingBounds!.Value, "toolpath");
        Assert.Equal(golden.GetProperty("toolNumbers").EnumerateArray().Select(item => item.GetInt32()), nc.ToolNumbers);
        Assert.Equal(golden.GetProperty("usesRotaryAxes").GetBoolean(), nc.UsesRotaryAxes);
        Assert.Equal(golden.GetProperty("usesTiltedPlane").GetBoolean(), nc.UsesTiltedPlane);
        var stock = MeshFiles.Read(Path.Combine(folder, manifest.Files!.Stock![0].Path)).Bounds!.Value;
        Assert.True(stock.Inflate(5).Intersects(nc.CuttingBounds.Value));
    }

    [Fact]
    public void Matrix_and_euler_must_agree_when_both_are_given()
    {
        // The exporter definition §5 example: matrix of Rz(90) but rotationDeg also says y = 15.
        var inconsistent = PartModel(125.4, -80, 42.7, 0, 15, 90, matrix: [0, -1, 0, 125.4, 1, 0, 0, -80, 0, 0, 1, 42.7, 0, 0, 0, 1]);
        var context = service.Resolve(new(inconsistent, "mm"), null, null);
        Assert.False(context.IsResolved);
        Assert.Contains(context.Diagnostics, item => item.Code == PackageDiagnosticCodes.UcsMatrixInconsistent);

        var consistent = PartModel(125.4, -80, 42.7, 0, 0, 90, matrix: [0, -1, 0, 125.4, 1, 0, 0, -80, 0, 0, 1, 42.7, 0, 0, 0, 1]);
        var ok = service.Resolve(new(consistent, "mm"), null, null);
        Assert.True(ok.IsResolved);
        Near(new(0, 0, 0), ok.FileToNc(new(125.4, -80, 42.7)), 1e-9);
        // UCS X axis is model +Y: the model point origin + (0, 10, 0) is UCS (10, 0, 0).
        Near(new(10, 0, 0), ok.FileToNc(new(125.4, -70, 42.7)), 1e-9);
    }

    [Fact]
    public void Matrix_only_ucs_is_accepted_and_a_mirrored_matrix_is_refused()
    {
        var matrixOnly = new CoordinateSystemDefinition
        {
            Type = CoordinateSystemTypes.PartModelCoordinates, Name = "UCS_M", FilesAreIn = CoordinateSystemTypes.FilesInPartModelCoordinates,
            Matrix = [0, 0, 1, 80, 0, 1, 0, 0, -1, 0, 0, 40, 0, 0, 0, 1]
        };
        Assert.True(service.Resolve(new(matrixOnly, "mm"), null, null).IsResolved);

        var mirrored = matrixOnly with { Matrix = [-1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1] };
        var refused = service.Resolve(new(mirrored, "mm"), null, null);
        Assert.Contains(refused.Diagnostics, item => item.Code == PackageDiagnosticCodes.UcsMatrixInvalid);
    }

    [Fact]
    public void Unknown_or_missing_coordinate_systems_are_never_guessed()
    {
        Assert.Contains(service.Resolve(new(null, "mm"), null, null).Diagnostics, item => item.Code == PackageDiagnosticCodes.UcsMissing);
        Assert.Contains(service.Resolve(new(new CoordinateSystemDefinition { Type = "machine-coordinates", Name = "X" }, "mm"), null, null).Diagnostics,
            item => item.Code == PackageDiagnosticCodes.UcsTypeUnknown);
        Assert.Contains(service.Resolve(new(PartModel(0, 0, 0, 0, 0, 0, order: "XYZ"), "mm"), null, null).Diagnostics,
            item => item.Code == PackageDiagnosticCodes.UcsTransformIncomplete);
        var modeA = new CoordinateSystemDefinition
        {
            Type = CoordinateSystemTypes.NcProcedureUcs, Name = "UCS", FilesAreIn = CoordinateSystemTypes.FilesInNcWorkCoordinates,
            OriginMm = new(10, 0, 0)
        };
        Assert.Contains(service.Resolve(new(modeA, "mm"), null, null).Diagnostics, item => item.Code == PackageDiagnosticCodes.CoordinateModeConflict);
        Assert.Contains(service.Resolve(new(modeA with { OriginMm = null }, "furlong"), null, null).Diagnostics, item => item.Code == PackageDiagnosticCodes.UnitsUnknown);
    }

    [Fact]
    public void Inch_files_are_scaled_to_millimetres_before_the_ucs()
    {
        var context = service.Resolve(new(PartModel(25.4, 0, 0, 0, 0, 0), "inch"), null, null);
        Assert.Equal(25.4, context.FileUnitScale);
        Near(new(25.4, 0, 0), context.FileToNc(new(2, 0, 0)), 1e-9);
    }

    [Fact]
    public void Measured_work_offset_maps_nc_zero_into_machine_coordinates_separately()
    {
        var program = new CoordinateProgram(new CoordinateSystemDefinition
        {
            Type = CoordinateSystemTypes.NcProcedureUcs, Name = "UCS_OP010", WorkOffset = "G54",
            FilesAreIn = CoordinateSystemTypes.FilesInNcWorkCoordinates, Transform = "identity"
        }, "mm");
        var measured = new CoordinateMachine("haas-vf3-01", "G54", new(-350.25, -200.5, -410.125), new(0, 0, 0));
        var context = service.Resolve(program, null, measured);
        Near(new(-340.25, -190.5, -410.125), context.FileToMachine(new(10, 10, 0))!.Value);
        Assert.Empty(context.Diagnostics);

        var wrongOffset = service.Resolve(program, null, measured with { WorkOffset = "G55" });
        Assert.Contains(wrongOffset.Diagnostics, item => item.Code == PackageDiagnosticCodes.WorkOffsetMismatch && item.Severity == DiagnosticSeverity.Warning);
    }

    private static void AssertBox(JsonElement expected, BoundingBox actual, string what)
    {
        var min = expected[0];
        var max = expected[1];
        Near(new(min[0].GetDouble(), min[1].GetDouble(), min[2].GetDouble()), actual.Min, 1e-4);
        Near(new(max[0].GetDouble(), max[1].GetDouble(), max[2].GetDouble()), actual.Max, 1e-4);
        _ = what;
    }
}
