using System.Text.Json;
using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Presentation.NcViewer;
using Meimad.Planner.NcEngine;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

public sealed class NcViewerOperationToolTableTests
{
    private const string Editable = """
        {"schemaVersion":"1.0","name":"O1500 tools","units":"mm","machineType":"mill",
         "toolTypes":["end-mill","ball-mill","bull-nose-mill","face-mill","chamfer-mill","drill","tap","reamer","boring-head","probe","other"],
         "tools":[
           {"number":1,"cornerRadius":0,"tip":0,"type":"end-mill","diameter":10,"width":null,"length":null,"hand":"neutral","description":"from comment"},
           {"number":2,"cornerRadius":0,"tip":0,"type":"other","diameter":null,"width":null,"length":null,"hand":"neutral","description":""},
           {"number":3,"cornerRadius":0,"tip":0,"type":"end-mill","diameter":6,"width":null,"length":null,"hand":"neutral","description":"comment says end mill"},
           {"number":4,"cornerRadius":0,"tip":0,"type":"drill","diameter":6,"width":null,"length":null,"hand":"neutral","description":"drill 6"}]}
        """;

    [Fact]
    public void Tool_room_shapes_win_descriptions_come_from_the_table_and_unknown_tools_are_reported()
    {
        var table = NcViewerOperationToolTable.From(Preparation())!;
        Assert.Equal("Tool table r1 (tools.csv) + Tool Room v2", table.Source);
        // Every released row is a tool, measured or not; T7 is not in the program.
        Assert.Equal([1, 2, 3, 7], table.Tools.Select(tool => tool.Number));
        // Rows without a Tool Room shape (or without a diameter) are read from their description.
        Assert.Equal([3], table.DescriptionsToInfer.Select(row => row.Number));

        var readings = new Dictionary<int, NcEngineInferredTool>
        {
            [3] = new(3, "drill", 8.5, null, 0, 0, null, "DRILL 8.5MM")
        };
        var applied = table.ApplyTo(JsonDocument.Parse(Editable).RootElement, readings, out var warnings);

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
        // T3 has no Tool Room shape: the released description (read by the engine) replaces the comment.
        Assert.Equal("drill", tools[2]!["type"]!.GetValue<string>());
        Assert.Equal(8.5, tools[2]!["diameter"]!.GetValue<double>());
        Assert.Equal("DRILL 8.5MM", tools[2]!["description"]!.GetValue<string>());
        // T4 is used by the program but not released: its inferred values stay.
        Assert.Equal("drill", tools[3]!["type"]!.GetValue<string>());
        Assert.Equal(6, tools[3]!["diameter"]!.GetValue<double>());
        var warning = Assert.Single(warnings);
        Assert.Contains("T4", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("T7", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Released_rows_alone_give_descriptions_and_defer_to_the_engine_reading()
    {
        var rows = new PlannerReleasedTool[]
        {
            new("row-1", 1, "T1", "FLAT END MILL D12", true, true, true, "1"),
            new("row-2", 2, "T2", "DRILL 8.5MM", true, true, true, "2"),
            new("row-3", 3, "T3", "Retired", true, true, false, null)
        };
        var table = NcViewerOperationToolTable.FromRows(rows, 2, "tools.mht")!;
        Assert.Equal("Tool table r2 (tools.mht)", table.Source);
        Assert.Equal(0, table.PreparationVersion);
        Assert.Equal([1, 2], table.Tools.Select(tool => tool.Number));
        Assert.Equal([1, 2], table.DescriptionsToInfer.Select(row => row.Number));

        var readings = new Dictionary<int, NcEngineInferredTool>
        {
            [1] = new(1, "end-mill", 12, 75, 0, 0, null, "FLAT END MILL D12"),
            [2] = new(2, "other", null, null, 0, 0, null, "DRILL 8.5MM")
        };
        var applied = table.ApplyTo(JsonDocument.Parse(Editable).RootElement, readings, out _);
        var tools = applied["tools"]!.AsArray();
        Assert.Equal(12, tools[0]!["diameter"]!.GetValue<double>());
        Assert.Equal(75, tools[0]!["length"]!.GetValue<double>());
        Assert.Equal("FLAT END MILL D12", tools[0]!["description"]!.GetValue<string>());
        // A reading of "other" never downgrades the type the program implied.
        Assert.Equal("other", tools[1]!["type"]!.GetValue<string>());
        Assert.Equal("DRILL 8.5MM", tools[1]!["description"]!.GetValue<string>());
    }

    [Fact]
    public void The_catalog_supplies_the_rows_of_the_release_tool_table()
    {
        var rows = new PlannerReleasedTool[] { new("row-1", 1, "T1", "FACE MILL D80", true, true, true, "1") };
        var tools = new PlannerToolTableRelease("tools-2", 2, "tools.mht", 10, new string('a', 64), DateTimeOffset.UtcNow, "planner", "Second", 1, rows);
        var process = new PlannerProcessRevision("process-2", 2, true, DateTimeOffset.UtcNow, "planner", "Second", 1, tools);
        var catalog = new PlannerGCodeCatalog("operation-1", process, [process], [], []);

        var table = NcViewerOperationToolTable.FromCatalog(catalog, "tools-2");

        Assert.NotNull(table);
        Assert.Equal("Tool table r2 (tools.mht)", table.Source);
        Assert.Equal("FACE MILL D80", Assert.Single(table.Tools).Description);
        Assert.Null(NcViewerOperationToolTable.FromCatalog(catalog, "tools-9"));
        Assert.Null(NcViewerOperationToolTable.FromCatalog(null, "tools-2"));
    }

    [Fact]
    public void Inch_programs_receive_the_millimetre_values_converted_and_lathe_tables_use_turning_types()
    {
        var table = NcViewerOperationToolTable.From(Preparation())!;
        var inch = JsonDocument.Parse(Editable.Replace("\"units\":\"mm\"", "\"units\":\"inch\"", StringComparison.Ordinal)).RootElement;
        Assert.Equal(Math.Round(12.5 / 25.4, 4), table.ApplyTo(inch, null, out _)["tools"]![0]!["diameter"]!.GetValue<double>());

        var turning = NcViewerOperationToolTable.From(Preparation() with
        {
            Tools =
            [
                new(1, "T1", "Turning tool", true, "1", 1, 50, 20, "TURNING_TOOL", new Dictionary<string, double> { ["cornerRadius"] = 0.8 }, null, []),
                new(2, "T2", "Measured only", true, "2", 2, 60, 5, "OTHER", new Dictionary<string, double>(), null, [])
            ]
        })!;
        var lathe = JsonDocument.Parse(Editable
            .Replace("\"machineType\":\"mill\"", "\"machineType\":\"lathe\"", StringComparison.Ordinal)
            .Replace("\"toolTypes\":[\"end-mill\"", "\"toolTypes\":[\"external-cutter\",\"end-mill\"", StringComparison.Ordinal)).RootElement;
        var tools = turning.ApplyTo(lathe, null, out _)["tools"]!.AsArray();
        Assert.Equal("external-cutter", tools[0]!["type"]!.GetValue<string>());
        Assert.Equal(0.8, tools[0]!["cornerRadius"]!.GetValue<double>());
        Assert.Equal("other", tools[1]!["type"]!.GetValue<string>());
        Assert.Equal(5, tools[1]!["diameter"]!.GetValue<double>());
    }

    [Fact]
    public void Nothing_is_applied_without_a_table_or_without_released_rows()
    {
        Assert.Null(NcViewerOperationToolTable.From(null));
        Assert.Null(NcViewerOperationToolTable.From(Preparation() with { Tools = [] }));
        Assert.Null(NcViewerOperationToolTable.FromRows(null, 1, "tools.csv"));
        Assert.Null(NcViewerOperationToolTable.FromRows([], 1, "tools.csv"));
        // An unsaved preparation still carries every released row (descriptions), just no shapes.
        var unsaved = NcViewerOperationToolTable.From(Preparation() with { Version = 0 })!;
        Assert.Equal("Tool table r1 (tools.csv)", unsaved.Source);
        Assert.Equal(4, unsaved.Tools.Count);
    }

    private static PlannerToolPreparation Preparation() => new(
        "operation-1", "machine-1", "M01", "Mill", "mill", "HAAS_NGC", "RADIUS", "tools-1", 1, "tools.csv",
        2, "prep-2", DateTimeOffset.Parse("2026-09-24T09:00:00Z"), "tool-room-1", null, null, "tools-1",
        [
            new(1, "T1", "Bull nose D12.5", true, "1", 1, 101.25, 12.5, "BULL_NOSE_END_MILL",
                new Dictionary<string, double> { ["cuttingDiameter"] = 12.5, ["cornerRadius"] = 1.5 }, null, []),
            new(2, "T2", "Ball D8", true, "2", 2, null, null, "BALL_END_MILL",
                new Dictionary<string, double> { ["cuttingDiameter"] = 8 }, null, []),
            new(3, "T3", "DRILL 8.5MM", false, null, null, null, null, "OTHER", new Dictionary<string, double>(), null, []),
            new(4, "T7", "Drill 6.8", true, "7", 7, 80, 6.8, "DRILL", new Dictionary<string, double>(), null, [])
        ]);
}
