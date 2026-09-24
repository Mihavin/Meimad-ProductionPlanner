using System.Text.Json;
using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Presentation.NcViewer;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

public sealed class NcViewerToolRoomTableTests
{
    private const string Editable = """
        {"schemaVersion":"1.0","name":"O1500 tools","units":"mm","machineType":"mill",
         "toolTypes":["end-mill","ball-mill","bull-nose-mill","face-mill","chamfer-mill","drill","tap","reamer","boring-head","probe","other"],
         "tools":[
           {"number":1,"cornerRadius":0,"tip":0,"type":"end-mill","diameter":10,"width":null,"length":null,"hand":"neutral","description":"from comment"},
           {"number":2,"cornerRadius":0,"tip":0,"type":"other","diameter":null,"width":null,"length":null,"hand":"neutral","description":""},
           {"number":4,"cornerRadius":0,"tip":0,"type":"drill","diameter":6,"width":null,"length":null,"hand":"neutral","description":"drill 6"}]}
        """;

    [Fact]
    public void Tool_room_values_replace_the_program_tools_and_unknown_tools_are_reported()
    {
        var table = NcViewerToolRoomTable.From(Preparation())!;
        Assert.Equal("Tool Room tool table v2", table.Source);
        // T1 measured, T2 described by shape only, T3 has nothing prepared, T7 is not in the program.
        Assert.Equal([1, 2, 7], table.Tools.Select(tool => tool.Number));

        var applied = table.ApplyTo(JsonDocument.Parse(Editable).RootElement, out var warnings);

        var tools = applied["tools"]!.AsArray();
        Assert.Equal("bull-nose-mill", tools[0]!["type"]!.GetValue<string>());
        Assert.Equal(12.5, tools[0]!["diameter"]!.GetValue<double>());
        Assert.Equal(101.25, tools[0]!["length"]!.GetValue<double>());
        Assert.Equal(1.5, tools[0]!["cornerRadius"]!.GetValue<double>());
        Assert.Equal("Bull nose D12.5", tools[0]!["description"]!.GetValue<string>());
        // Shape and cutting diameter without measurements still describe the cutter for the simulation.
        Assert.Equal("ball-mill", tools[1]!["type"]!.GetValue<string>());
        Assert.Equal(8, tools[1]!["diameter"]!.GetValue<double>());
        Assert.Equal(4, tools[1]!["cornerRadius"]!.GetValue<double>());
        Assert.Null(tools[1]!["length"]);
        // T4 is used by the program but unknown to the Tool Room: its inferred values stay.
        Assert.Equal("drill", tools[2]!["type"]!.GetValue<string>());
        Assert.Equal(6, tools[2]!["diameter"]!.GetValue<double>());
        var warning = Assert.Single(warnings);
        Assert.Contains("T4", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("T7", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Inch_programs_receive_the_millimetre_measurements_converted()
    {
        var table = NcViewerToolRoomTable.From(Preparation())!;
        var inch = JsonDocument.Parse(Editable.Replace("\"units\":\"mm\"", "\"units\":\"inch\"", StringComparison.Ordinal)).RootElement;

        var applied = table.ApplyTo(inch, out _);

        Assert.Equal(Math.Round(12.5 / 25.4, 4), applied["tools"]![0]!["diameter"]!.GetValue<double>());
    }

    [Fact]
    public void Lathe_tables_use_turning_tool_types_and_other_shapes_keep_the_inferred_type()
    {
        var preparation = Preparation() with
        {
            Tools =
            [
                new(1, "T1", "Turning tool", true, "1", 1, 50, 20, "TURNING_TOOL", new Dictionary<string, double> { ["cornerRadius"] = 0.8 }, null, []),
                new(2, "T2", "Measured only", true, "2", 2, 60, 5, "OTHER", new Dictionary<string, double>(), null, [])
            ]
        };
        var table = NcViewerToolRoomTable.From(preparation)!;
        var lathe = JsonDocument.Parse(Editable
            .Replace("\"machineType\":\"mill\"", "\"machineType\":\"lathe\"", StringComparison.Ordinal)
            .Replace("\"toolTypes\":[\"end-mill\"", "\"toolTypes\":[\"external-cutter\",\"end-mill\"", StringComparison.Ordinal)).RootElement;

        var applied = table.ApplyTo(lathe, out _);

        var tools = applied["tools"]!.AsArray();
        Assert.Equal("external-cutter", tools[0]!["type"]!.GetValue<string>());
        Assert.Equal(0.8, tools[0]!["cornerRadius"]!.GetValue<double>());
        Assert.Equal("other", tools[1]!["type"]!.GetValue<string>());
        Assert.Equal(5, tools[1]!["diameter"]!.GetValue<double>());
    }

    [Fact]
    public void Nothing_is_applied_before_the_first_save_or_without_prepared_tools()
    {
        Assert.Null(NcViewerToolRoomTable.From(null));
        Assert.Null(NcViewerToolRoomTable.From(Preparation() with { Version = 0 }));
        Assert.Null(NcViewerToolRoomTable.From(Preparation() with
        {
            Tools = [new(1, "T1", "Nothing", true, "1", null, null, null, "OTHER", new Dictionary<string, double>(), null, [])]
        }));
    }

    private static PlannerToolPreparation Preparation() => new(
        "operation-1", "machine-1", "M01", "Mill", "mill", "HAAS_NGC", "RADIUS", "tools-1", 1, "tools.csv",
        2, "prep-2", DateTimeOffset.Parse("2026-09-24T09:00:00Z"), "tool-room-1", null, null, "tools-1",
        [
            new(1, "T1", "Bull nose D12.5", true, "1", 1, 101.25, 12.5, "BULL_NOSE_END_MILL",
                new Dictionary<string, double> { ["cuttingDiameter"] = 12.5, ["cornerRadius"] = 1.5 }, null, []),
            new(2, "T2", "Ball D8", true, "2", 2, null, null, "BALL_END_MILL",
                new Dictionary<string, double> { ["cuttingDiameter"] = 8 }, null, []),
            new(3, "T3", "Nothing prepared", false, null, null, null, null, "OTHER", new Dictionary<string, double>(), null, []),
            new(4, "T7", "Drill 6.8", true, "7", 7, 80, 6.8, "DRILL", new Dictionary<string, double>(), null, [])
        ]);
}
