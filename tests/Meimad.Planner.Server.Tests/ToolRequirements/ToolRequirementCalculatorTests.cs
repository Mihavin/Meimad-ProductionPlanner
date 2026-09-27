using Meimad.Planner.Server.Application.GCode;
using Meimad.Planner.Server.Domain.ToolRequirements;

namespace Meimad.Planner.Server.Tests.ToolRequirements;

public sealed class ToolRequirementCalculatorTests
{
    private static readonly DateTimeOffset Monday = new(2026, 9, 28, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public void One_copy_migrates_between_machines_whose_uses_do_not_overlap()
    {
        var requirement = Assert.Single(ToolRequirementCalculator.Calculate(
        [
            Use("M-05", "op-1", 0, 4),
            Use("M-07", "op-2", 5, 9),
            Use("M-05", "op-3", 10, 12)
        ]));

        Assert.Equal(1, requirement.CopiesNeeded);
        Assert.Equal(["M-05", "M-07"], requirement.Machines);
        Assert.Equal(2, requirement.MachineChanges);
        var route = Assert.Single(requirement.Routes);
        Assert.Equal(["M-05", "M-07", "M-05"], route.Stops.Select(stop => stop.MachineLabel));
    }

    [Fact]
    public void Simultaneous_uses_on_two_machines_need_two_copies_and_each_stays_on_its_machine()
    {
        var requirement = Assert.Single(ToolRequirementCalculator.Calculate(
        [
            Use("M-05", "op-1", 0, 6),
            Use("M-07", "op-2", 2, 8),
            Use("M-05", "op-3", 6, 10),
            Use("M-07", "op-4", 8, 12)
        ]));

        Assert.Equal(2, requirement.CopiesNeeded);
        Assert.Equal(0, requirement.MachineChanges);
        Assert.All(requirement.Routes, route => Assert.Single(route.Stops));
        Assert.Equal(Monday, requirement.FirstNeed);
        Assert.Equal(Monday.AddHours(12), requirement.LastNeed);
    }

    [Fact]
    public void A_tool_listed_twice_in_one_tool_table_needs_two_copies()
    {
        var requirement = Assert.Single(ToolRequirementCalculator.Calculate([Use("M-05", "op-1", 0, 4) with { Copies = 2 }]));

        Assert.Equal(2, requirement.CopiesNeeded);
    }

    [Fact]
    public void Materials_and_diameters_keep_tools_apart_while_spelling_does_not()
    {
        var requirements = ToolRequirementCalculator.Calculate(
        [
            Use("M-05", "op-1", 0, 4),
            Use("M-07", "op-2", 0, 4) with { MaterialGroup = MaterialGroups.Titanium },
            Use("M-09", "op-3", 5, 8) with { ToolName = "fin 10" },
            Use("M-11", "op-4", 0, 4) with { Diameter = 12 }
        ]);

        Assert.Equal(3, requirements.Count);
        var aluminum = requirements.Single(requirement => requirement.MaterialGroup == MaterialGroups.Aluminum && requirement.Diameter == 10);
        Assert.Equal(1, aluminum.CopiesNeeded);
        Assert.Equal(2, aluminum.Uses.Count);
        Assert.Equal(MaterialGroups.Aluminum, requirements[0].MaterialGroup);
        Assert.Equal(MaterialGroups.Titanium, requirements[^1].MaterialGroup);
    }

    [Theory]
    [InlineData("AL 7050-T7451 AMS 4050H + USQ Plate 2.5 \"", MaterialGroups.Aluminum)]
    [InlineData("TI-6AL-4V-ANNEALED AMS 4911L Plate 1 \"", MaterialGroups.Titanium)]
    [InlineData("15-5PH CEM-SOL N HT AMS 5659M BAR-C 1.75 \"", MaterialGroups.Stainless)]
    [InlineData("INCONEL 718 BAR", MaterialGroups.Nickel)]
    [InlineData("4340 STEEL", MaterialGroups.Steel)]
    [InlineData("PEEK rod", MaterialGroups.Plastic)]
    [InlineData(null, MaterialGroups.Unknown)]
    public void Material_groups_are_read_from_the_material_text(string? material, string expected) =>
        Assert.Equal(expected, MaterialGroups.Classify(material));

    [Theory]
    [InlineData("FIN_10", "END_MILL", 10.0)]
    [InlineData("FIN_10_R1", "BULL_NOSE_END_MILL", 10.0)]
    [InlineData("MERASEK_16", "END_MILL", 16.0)]
    [InlineData("CADURI_6_AROH", "BALL_END_MILL", 6.0)]
    [InlineData("DRILL_2.5", "DRILL", 2.5)]
    [InlineData("MECADED_6.35", "SPOT_DRILL", 6.35)]
    [InlineData("FAZA 6", "CHAMFER_MILL", 6.0)]
    [InlineData("FLYCUTTER_80", "FACE_MILL", 80.0)]
    public void Tool_names_give_the_type_and_diameter(string name, string type, double diameter)
    {
        Assert.Equal(type, ToolNaming.TypeFromName(name));
        Assert.Equal(diameter, ToolNaming.DiameterFromName(name));
    }

    [Fact]
    public async Task The_cimatron_tool_report_gives_diameter_stick_out_and_holder()
    {
        var path = Path.Combine(RepositoryRoot(), "data", "sample data for testing", "UTILL",
            "30p450025601-001_nc1.DIR", "FANUC_4X", "TP_MODEL.TOOLS.mht");

        var tools = await ReleasedToolTableParser.ReadGeometryAsync(path, Path.GetFileName(path), CancellationToken.None);

        Assert.Equal(8, tools.Count);
        Assert.All(tools, tool =>
        {
            Assert.Matches("^T[0-9]+$", tool.ToolIdentifier);
            Assert.True(tool.Diameter > 0);
            Assert.True(tool.Length > 0);
        });
        Assert.Contains(tools, tool => !string.IsNullOrWhiteSpace(tool.Holder));
    }

    private static ToolUse Use(string machine, string operation, int fromHour, int toHour) => new(
        MaterialGroups.Aluminum, "FIN_10", 10, "END_MILL", "name", 1, ["T10"], "HOLDER-50_NEW", 34,
        machine, machine, operation, "WO-1", "PN-1", 10, "Mill", "AL 7075", Monday.AddHours(fromHour), Monday.AddHours(toHour));

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "data", "sample data for testing"))) return directory.FullName;
        }
        throw new DirectoryNotFoundException("Could not locate the repository sample-data root.");
    }
}
