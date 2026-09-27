using Meimad.Planner.Server.Application.GCode;

namespace Meimad.Planner.Server.Tests.GCode;

public sealed class NcDialectToolOffsetTests
{
    private static readonly NcToolOffset[] Offsets =
    [
        new(1, "T1", "FLAT END MILL D10 (4 FL)", 120.5, 10),
        new(12, "T12", "DRILL 8.4", 98.1234, 8.4)
    ];

    [Theory]
    [InlineData("HAAS_NGC")]
    [InlineData("FANUC_MACRO_B")]
    [InlineData("MAZAK_MATRIX_EIA")]
    public void Macro_B_family_writes_G10_length_and_cutter_geometry_and_resets_wear(string dialectId)
    {
        var dialect = NcDialects.Profile(dialectId);

        var radius = dialect.ToolOffsetLines(Offsets, diameterAsRadius: true, turning: false)!;
        var diameter = dialect.ToolOffsetLines(Offsets, diameterAsRadius: false, turning: false)!;

        Assert.Equal("G21", radius[0]);
        Assert.Equal("G10 L10 P1 R120.5 (T1 FLAT END MILL D10 4 FL LENGTH)", radius[1]);
        Assert.Equal("G10 L11 P1 R0. (T1 FLAT END MILL D10 4 FL LENGTH WEAR)", radius[2]);
        Assert.Equal("G10 L12 P1 R5. (T1 FLAT END MILL D10 4 FL RADIUS)", radius[3]);
        Assert.Equal("G10 L13 P1 R0. (T1 FLAT END MILL D10 4 FL DIAMETER WEAR)", radius[4]);
        Assert.Equal("G10 L10 P12 R98.1234 (T12 DRILL 8.4 LENGTH)", radius[5]);
        Assert.Equal("G10 L12 P12 R4.2 (T12 DRILL 8.4 RADIUS)", radius[7]);
        Assert.Equal("G10 L12 P1 R10. (T1 FLAT END MILL D10 4 FL DIAMETER)", diameter[3]);
        Assert.Equal(9, radius.Count);

        var program = dialect.ToolOffsetProgram(["(PRODUCTION PACKAGE 123456)"], radius);
        Assert.Equal("%", program[0]);
        Assert.Equal("O01991 (MEIMAD MEASURED TOOL OFFSETS)", program[1]);
        Assert.Equal("(PRODUCTION PACKAGE 123456)", program[2]);
        Assert.Equal("M30", program[^3]);
        Assert.Equal("tool-offsets/O01991.nc", dialect.ToolOffsetProgramLogicalPath);
    }

    [Fact]
    public void FANUC_lathe_writes_geometry_with_P_10000_plus_offset_and_other_lathes_have_no_syntax()
    {
        var fanuc = NcDialects.Profile("FANUC_MACRO_B").ToolOffsetLines(Offsets, diameterAsRadius: true, turning: true)!;
        Assert.Equal(["G21", "G10 P10001 X10. Z120.5 (T1 FLAT END MILL D10 4 FL)", "G10 P10012 X8.4 Z98.1234 (T12 DRILL 8.4)"], fanuc);

        Assert.Null(NcDialects.Profile("HAAS_NGC").ToolOffsetLines(Offsets, diameterAsRadius: true, turning: true));
        Assert.Null(NcDialects.Profile("MAZAK_MATRIX_EIA").ToolOffsetLines(Offsets, diameterAsRadius: true, turning: true));
    }

    [Fact]
    public void Okuma_OSP_assigns_tool_offset_system_variables_for_mills_and_lathes()
    {
        var dialect = NcDialects.Profile("OKUMA_OSP");

        var mill = dialect.ToolOffsetLines(Offsets, diameterAsRadius: false, turning: false)!;
        var lathe = dialect.ToolOffsetLines(Offsets, diameterAsRadius: false, turning: true)!;

        Assert.Equal(["VTOFH[1]=120.5 (T1 FLAT END MILL D10 4 FL LENGTH)", "VTOFD[1]=10. (T1 FLAT END MILL D10 4 FL DIAMETER)",
            "VTOFH[12]=98.1234 (T12 DRILL 8.4 LENGTH)", "VTOFD[12]=8.4 (T12 DRILL 8.4 DIAMETER)"], mill);
        Assert.Equal(["VTOFX[1]=10. (T1 FLAT END MILL D10 4 FL X)", "VTOFZ[1]=120.5 (T1 FLAT END MILL D10 4 FL Z)",
            "VTOFX[12]=8.4 (T12 DRILL 8.4 X)", "VTOFZ[12]=98.1234 (T12 DRILL 8.4 Z)"], lathe);
        Assert.DoesNotContain(mill, line => line.StartsWith("G21", StringComparison.Ordinal));

        var program = dialect.ToolOffsetProgram(["(PRODUCTION PACKAGE 123456)"], mill);
        Assert.Equal("(MEIMAD MEASURED TOOL OFFSETS)", program[0]);
        Assert.Equal("M02", program[^2]);
        Assert.DoesNotContain(program, line => line.StartsWith('O') || line == "%");
        Assert.Equal("tool-offsets/O1991.MIN", dialect.ToolOffsetProgramLogicalPath);
    }

    [Fact]
    public void Offset_values_always_carry_a_decimal_point_and_comments_keep_only_safe_characters()
    {
        var lines = NcDialects.Profile("HAAS_NGC").ToolOffsetLines(
            [new(3, "T3", "TAP M6x1 \"fine\" (spiral)", 75, 6)], diameterAsRadius: false, turning: false)!;

        Assert.Equal("G10 L10 P3 R75. (T3 TAP M6x1 fine spiral LENGTH)", lines[1]);
        Assert.Equal("G10 L12 P3 R6. (T3 TAP M6x1 fine spiral DIAMETER)", lines[3]);
    }
}
