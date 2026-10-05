using Meimad.Planner.NcEngine;

namespace Meimad.Planner.Server.Tests.NcEngine;

public sealed class NcSubprogramCallsTests
{
    [Fact]
    public void Calls_are_read_like_the_engine_reads_them()
    {
        string[] lines =
        [
            "(M98 P5555 IN A COMMENT)",
            "M98 P1001 L2",
            "N20 M98 P00031002 ; FANUC: repeat 3, program 1002",
            "G65 P9810 Z-5.",
            "M97 P100 (local N block, no file)",
            "M98P1001",
            "#100=98 P7"
        ];

        Assert.Equal([1001, 1002, 9810], NcSubprogramCalls.CalledPrograms(lines));
    }

    [Theory]
    [InlineData("pocket.nc", new[] { "%", "(POCKET)", "O1001 (ROUGH)" }, 1001)]
    [InlineData("pocket.nc", new[] { "%", "G90", "O1001" }, null)]
    [InlineData("pocket.nc", new[] { "%", "O1001 (ROUGH)", "G1 X1." }, 1001)]
    [InlineData("pocket.nc", new[] { ":2002" }, 2002)]
    [InlineData("O09810_probe.nc", new[] { "(PROBE)", "G65 P9811" }, 9810)]
    [InlineData("finish.nc", new[] { "G1 X1." }, null)]
    public void A_file_declares_its_number_on_its_first_code_line_or_in_its_name(
        string fileName, string[] lines, int? expected)
    {
        Assert.Equal(expected, NcSubprogramCalls.ProgramNumber(fileName, lines));
    }

    [Fact]
    public void Missing_programs_follow_nested_calls_through_the_included_files()
    {
        var missing = NcSubprogramCalls.MissingPrograms(
            ["M98 P1001", "G65 P9810"],
            [(1001, ["O1001", "M98 P1002", "M98 P1003", "M99"]), (1003, ["O1003", "M99"]), (null, ["M98 P4000"])]);

        Assert.Equal([9810, 1002], missing);
    }

    [Fact]
    public void A_called_program_is_found_by_the_engine_names_then_by_its_own_number()
    {
        var folder = Path.Combine(Path.GetTempPath(), "MeimadPlanner.Subprograms.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var main = Path.Combine(folder, "main.nc");
            File.WriteAllText(main, "O1001\nM98 P1002\nM30\n");
            File.WriteAllText(Path.Combine(folder, "O01002.nc"), "O1002\nM99\n");
            File.WriteAllText(Path.Combine(folder, "rough.nc"), "%\nO1003 (ROUGH)\nM99\n");

            Assert.Equal("O01002.nc", Path.GetFileName(NcSubprogramCalls.FindInFolder(folder, 1002)));
            Assert.Equal("rough.nc", Path.GetFileName(NcSubprogramCalls.FindInFolder(folder, 1003)));
            // The main program itself is never its own subprogram.
            Assert.Null(NcSubprogramCalls.FindInFolder(folder, 1001, excludePath: main));
            Assert.Null(NcSubprogramCalls.FindInFolder(folder, 4000));

            // Mazak EIA programs, by name and by declared number.
            File.WriteAllText(Path.Combine(folder, "2001.EIA"), "O2001\nM99\n");
            File.WriteAllText(Path.Combine(folder, "PROBE.EIA"), "O9013(PROBE)\nM99\n");
            Assert.Equal("2001.EIA", Path.GetFileName(NcSubprogramCalls.FindInFolder(folder, 2001)));
            Assert.Equal("PROBE.EIA", Path.GetFileName(NcSubprogramCalls.FindInFolder(folder, 9013)));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
