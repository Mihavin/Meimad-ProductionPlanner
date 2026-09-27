using System.Text;
using Meimad.Planner.NcEngine;

namespace Meimad.Planner.Server.Tests.NcEngine;

public sealed class NcEngineRuntimeTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "MeimadPlanner.NcEngineRuntime.Tests", Guid.NewGuid().ToString("N"));
    private readonly NcEngineRuntime runtime = new();

    public NcEngineRuntimeTests() => Directory.CreateDirectory(folder);

    [Fact]
    public void Vendored_and_meimad_machine_definitions_load()
    {
        Assert.Equal(
        [
            "chevalier-flc-200mc", "doosan-dvf-5000", "fanuc-0i-mc-vmc-3axis", "fanuc-0i-mc-vmc-4axis-a",
            "haas-st-25y", "haas-umc-500", "haas-vf-3ss", "mazak-variaxis-i-500", "okuma-genos-l200e-m"
        ], runtime.Machines.Select(machine => machine.Id).Order(StringComparer.Ordinal));
        Assert.Equal("okuma-osp-lathe", runtime.Machines.Single(machine => machine.Id == "okuma-genos-l200e-m").Translation);
        Assert.Equal("haas-lathe", runtime.Machines.Single(machine => machine.Id == "haas-st-25y").Translation);
        Assert.Equal("mazak-tilt-a-as-b", runtime.Machines.Single(machine => machine.Id == "mazak-variaxis-i-500").Translation);
        Assert.Null(runtime.Machines.Single(machine => machine.Id == "haas-vf-3ss").Translation);

        // The catalog read without V8 lists the same machines (Setup list, Server validation).
        var catalog = NcEngineMachineCatalog.Load();
        Assert.Equal(runtime.Machines.Select(machine => machine.Id).Order(StringComparer.Ordinal),
            catalog.Machines.Select(machine => machine.Id).Order(StringComparer.Ordinal));
        Assert.True(catalog.Find("mazak-variaxis-i-500") is { Type: "mill", BuiltIn: false, Translation: "mazak-tilt-a-as-b" });
        Assert.True(catalog.Find("haas-umc-500") is { BuiltIn: true });
        Assert.False(catalog.Contains("auto"));
    }

    [Fact]
    public void Okuma_osp_lathe_program_is_translated_and_every_row_maps_back_to_the_program()
    {
        var memory = Path.Combine(folder, "OkumaMemory");
        Directory.CreateDirectory(memory);
        File.WriteAllText(Path.Combine(memory, "O0500.MIN"), "$O0500.MIN%\nG00 X80 Z2\nG01 X40 F0.2\nG04 F0.5\nRTS\n%\n");
        var program = string.Join("\n",
        [
            "$MAIN.MIN%", "G50 S2000", "T010101", "G96 S150 M03 G95", "G00 X62 Z2",
            "G85 NLAP1 D2 U0.4 W0.1 F0.3", "NLAP1 G81", "G00 X20", "G01 Z0 F0.15", "X50 Z-30", "X62", "G80",
            "G87 NLAP1", "G00 X100 Z50", "CALL O0500 Q2", "G04 F1.5", "VC1=1", "IF [VC1 EQ 1] NEND",
            "G00 X200", "NEND M02", ""
        ]);
        runtime.SetReadableFolders([memory]);
        var request = new NcEnginePreviewRequest(program, MachineSelection: "okuma-genos-l200e-m",
            ProgramMemory: new Dictionary<string, string> { ["okuma-genos-l200e-m"] = memory });

        var prepared = runtime.PrepareForTesting(request);
        var preview = runtime.ParsePreview(request);
        var analysis = runtime.Analyze(new NcEngineAnalysisRequest(program, "OKUMA_OSP", "okuma-genos-l200e-m",
            ProgramMemory: new Dictionary<string, string> { ["okuma-genos-l200e-m"] = memory }));

        Assert.Equal("okuma-osp-lathe", prepared.GetProperty("translation").GetString());
        var lines = prepared.GetProperty("lines").EnumerateArray().Select(line => line.GetString()!).ToArray();
        var map = prepared.GetProperty("map").EnumerateArray().ToArray();
        Assert.Equal(lines.Length, map.Length);
        Assert.Contains(lines, line => line.StartsWith("G71 P", StringComparison.Ordinal));      // LAP -> two-block G71
        Assert.Contains(lines, line => line.StartsWith("G70 P", StringComparison.Ordinal));      // G87 -> G70
        Assert.Contains("T0101", lines);                                                           // six-digit T word
        Assert.Contains("IF [#501 EQ 1] GOTO 90001", lines);                                       // VC and named label (NLAP1 = 90000, NEND = 90001)
        Assert.Contains(lines, line => line.StartsWith("(M98 P500 -> O0500.MIN x2)", StringComparison.Ordinal));
        Assert.All(map, entry => Assert.InRange(entry.GetProperty("line").GetInt32(), 1, program.Split('\n').Length));
        // Both LAP blocks report the row of the single G85 line; inlined rows report the CALL line.
        var callRow = Array.IndexOf(program.Split('\n'), "CALL O0500 Q2") + 1;
        Assert.Equal(2, map.Count(entry => entry.GetProperty("line").GetInt32() == Array.IndexOf(program.Split('\n'), "G85 NLAP1 D2 U0.4 W0.1 F0.3") + 1));
        Assert.Contains(map, entry => entry.GetProperty("line").GetInt32() == callRow
            && entry.TryGetProperty("unit", out var unit) && unit.GetString() == "O0500.MIN");

        Assert.Equal("lathe", preview.Summary.Kind);
        Assert.Equal("okuma-genos-l200e-m", preview.Summary.Machine?.Id);
        Assert.True(preview.Summary.SegmentCount > 10);
        Assert.Contains("O0500.MIN", preview.Packed, StringComparison.Ordinal);
        Assert.Contains("Program memory", preview.Packed, StringComparison.Ordinal);
        Assert.Equal("okuma-osp-lathe", analysis.Translation);
        Assert.Equal(new[] { "O0500.MIN" }, analysis.Subprograms);
        Assert.Equal(1.5 + 2 * 0.5, analysis.DwellSeconds, 6);
        Assert.Equal(program.Split('\n').Length, analysis.LineCount);
        Assert.True(analysis.FeedSeconds > 0);
    }

    [Fact]
    public void Haas_lathe_one_block_cycles_are_expanded_for_the_st_25y()
    {
        var program = "%\nO2000\nG18 G20 G99\nG50 S2500\nT101\nG96 S500 M03\nG0 X2.6 Z0.1\n"
            + "G71 P10 Q20 D0.1 U0.02 W0.005 F0.012\nN10 G0 X1.\nG1 Z0 F0.006\nX2.5 Z-1.\nN20 X2.6\nG70 P10 Q20\n"
            + "G0 X6. Z4.\nG92 X2.4 Z-0.8 F0.05\nX2.38\nG0 X6. Z4.\nM97 P100\nM30\nN100\nG1 X5.9 F0.01\nM99\n%\n";

        var analysis = runtime.Analyze(new NcEngineAnalysisRequest(program, "HAAS_NGC"));

        Assert.Equal("haas-st-25y", analysis.MachineId);            // HAAS_NGC lathe programs get the Haas lathe
        Assert.Equal("haas-lathe", analysis.Translation);
        Assert.Contains(analysis.Warnings, warning => warning.StartsWith("Haas one-block G71 converted", StringComparison.Ordinal));
        Assert.Contains(analysis.Warnings, warning => warning.StartsWith("G70 finish: expanded N10-N20", StringComparison.Ordinal));
        Assert.Equal("inch", analysis.Units);
        Assert.True(analysis.SegmentCount > 12, $"segments: {analysis.SegmentCount}");
        Assert.Empty(analysis.Errors);
    }

    [Fact]
    public void Mazak_variaxis_tilt_a_and_fanuc_four_axis_a_programs_parse_on_the_meimad_mills()
    {
        var mazak = runtime.Analyze(new NcEngineAnalysisRequest(
            "%\nO3000\nG21 G17 G90 G94\nT1 M6\nG54 G0 X0 Y0 A0 C0\nG43 Z100. H1\nG0 A-30. C45.\nG1 Z10. F500.\nG1 X50. F800.\nG0 Z100.\nM30\n%\n",
            "MAZAK_MATRIX_EIA", "mazak-variaxis-i-500"));
        var fourAxis = runtime.Analyze(new NcEngineAnalysisRequest(
            "%\nO4000\nG21 G17 G90 G94\nT1 M6\nG54 G0 X0 Y0 A0\nG43 Z100. H1\nG0 A90.\nG1 Z10. F500.\nG1 X50. A180. F800.\nG0 Z100.\nM30\n%\n",
            "FANUC_MACRO_B", "fanuc-0i-mc-vmc-4axis-a"));

        Assert.Equal("mazak-tilt-a-as-b", mazak.Translation);
        Assert.Contains(mazak.Warnings, warning => warning.Contains("A tilt axis is interpreted as B", StringComparison.Ordinal));
        Assert.Equal(1, mazak.ToolChangeCount);
        Assert.InRange(mazak.FeedSeconds, 14, 16);       // 90 mm at 500 + 50 mm at 800 mm/min
        Assert.Empty(mazak.Errors);

        Assert.Null(fourAxis.Translation);
        Assert.Equal("fanuc-0i-mc-vmc-4axis-a", fourAxis.MachineId);
        Assert.True(fourAxis.SegmentCount >= 4);
        Assert.Empty(fourAxis.Errors);
    }

    [Fact]
    public void Dialect_picks_a_meimad_machine_when_the_program_names_none()
    {
        const string lathe = "G50 S2000\nG96 S150 M03\nT0101\nG99\nG0 X50. Z2.\nG1 Z-20. F0.2\nM30\n";
        const string mill = "G21 G17 G90\nT1 M6\nG0 X0 Y0 Z50.\nG1 Z-5. F100.\nM30\n";

        Assert.Equal("okuma-genos-l200e-m", runtime.Analyze(new NcEngineAnalysisRequest(lathe, "OKUMA_OSP")).MachineId);
        Assert.Equal("haas-st-25y", runtime.Analyze(new NcEngineAnalysisRequest(lathe, "HAAS_NGC")).MachineId);
        Assert.Equal("chevalier-flc-200mc", runtime.Analyze(new NcEngineAnalysisRequest(lathe, "FANUC_MACRO_B")).MachineId);
        Assert.Equal("mazak-variaxis-i-500", runtime.Analyze(new NcEngineAnalysisRequest(mill, "MAZAK_MATRIX_EIA")).MachineId);
        Assert.Equal("haas-umc-500", runtime.Analyze(new NcEngineAnalysisRequest(mill, "HAAS_NGC")).MachineId);
        Assert.Equal("doosan-dvf-5000", runtime.Analyze(new NcEngineAnalysisRequest(mill, "FANUC_MACRO_B")).MachineId);
        // An explicit machine wins; an uninstalled one falls back to detection with a reason.
        Assert.Equal("haas-vf-3ss", runtime.Analyze(new NcEngineAnalysisRequest(mill, "FANUC_MACRO_B", "haas-vf-3ss")).MachineId);
        var missing = runtime.Analyze(new NcEngineAnalysisRequest(mill, "HAAS_NGC", "no-such-machine"));
        Assert.Equal("haas-umc-500", missing.MachineId);
        Assert.StartsWith("NC viewer machine \"no-such-machine\" is not installed", missing.SelectionReason, StringComparison.Ordinal);
    }

    // Expected values are what Node.js path.win32 returns for the same calls.
    [Theory]
    [InlineData("path.join('C:\\\\a', 'b', '..', 'c.nc')", "C:\\a\\c.nc")]
    [InlineData("path.join('C:/a/', './b')", "C:\\a\\b")]
    [InlineData("path.resolve('C:\\\\a\\\\b', '..\\\\c')", "C:\\a\\c")]
    [InlineData("path.resolve('C:\\\\a', 'D:\\\\x', 'y')", "D:\\x\\y")]
    [InlineData("path.dirname('C:\\\\a\\\\b\\\\c.nc')", "C:\\a\\b")]
    [InlineData("path.dirname('C:\\\\a')", "C:\\")]
    [InlineData("path.basename('C:\\\\a\\\\O1000.NC', '.NC')", "O1000")]
    [InlineData("path.basename('\\\\\\\\server\\\\share\\\\dir\\\\file.nc')", "file.nc")]
    [InlineData("path.extname('C:\\\\a\\\\O1000.nc')", ".nc")]
    [InlineData("path.extname('C:\\\\a\\\\.hidden')", "")]
    [InlineData("path.relative('C:\\\\memory', 'C:\\\\memory\\\\sub\\\\O9013.nc')", "sub\\O9013.nc")]
    [InlineData("path.relative('C:\\\\memory\\\\sub', 'C:\\\\MEMORY')", "..")]
    [InlineData("path.relative('C:\\\\memory', 'D:\\\\other')", "D:\\other")]
    [InlineData("String(path.isAbsolute('C:\\\\a'))", "true")]
    [InlineData("String(path.isAbsolute('C:a'))", "false")]
    [InlineData("String(path.isAbsolute('\\\\\\\\server\\\\share'))", "true")]
    [InlineData("path.normalize('C:\\\\a\\\\.\\\\b\\\\..\\\\c\\\\')", "C:\\a\\c\\")]
    public void Path_shim_matches_node_win32(string expression, string expected)
    {
        var value = runtime.EvaluateForTesting($"(() => {{ const path = __meimadModules.path; return {expression}; }})()");

        Assert.Equal(expected, value);
    }

    [Fact]
    public void Engine_cannot_read_outside_its_folder_or_the_allowed_program_folders()
    {
        var secret = Path.Combine(folder, "secret.txt");
        File.WriteAllText(secret, "not for the engine");
        var check = $"__meimadModules.fs.existsSync({System.Text.Json.JsonSerializer.Serialize(secret)})";

        Assert.Equal(false, runtime.EvaluateForTesting(check));
        runtime.SetReadableFolders([folder]);
        Assert.Equal(true, runtime.EvaluateForTesting(check));
        runtime.SetReadableFolders([]);
        Assert.Equal(false, runtime.EvaluateForTesting(check));
        Assert.Equal(false, runtime.EvaluateForTesting(
            "__meimadModules.fs.existsSync('C:\\\\Windows\\\\win.ini')"));
    }

    [Fact]
    public void Macro_calls_resolve_from_the_machines_program_memory_folder()
    {
        var memory = Path.Combine(folder, "HaasMemory", "09000");
        Directory.CreateDirectory(memory);
        File.WriteAllText(Path.Combine(memory, "probe.nc"), "%\nO09013 (ORBIT)\nG1 X10. F600.\nM99\n%\n");
        var program = "O1000\nG21 G90 G17 G54\nT1 M6\nG0 X0 Y0 Z50.\nG65 P9013\nM30\n";
        runtime.SetReadableFolders([Path.Combine(folder, "HaasMemory")]);

        var result = runtime.ParsePreview(new NcEnginePreviewRequest(
            program,
            MachineSelection: "haas-umc-500",
            ProgramMemory: new Dictionary<string, string> { ["haas-umc-500"] = Path.Combine(folder, "HaasMemory") }));

        Assert.Equal("haas-umc-500", result.Summary.Machine?.Id);
        Assert.Contains("probe.nc", result.Packed, StringComparison.Ordinal);
        Assert.Equal(1d, result.Summary.EstimatedCycleSeconds!.Value - 2.8, 1); // 10 mm at 600 mm/min + tool change
    }

    [Fact]
    public void Runaway_script_is_interrupted_and_the_runtime_is_marked_faulted()
    {
        using var quick = new NcEngineRuntime(new NcEngineRuntimeOptions { CallTimeout = TimeSpan.FromMilliseconds(300) });

        Assert.Throws<NcEngineTimeoutException>(() => quick.EvaluateForTesting("while (true) {}"));
        Assert.True(quick.IsFaulted);
        Assert.Throws<NcEngineException>(() => quick.EvaluateForTesting("1"));
    }

    [Fact]
    public void Preview_model_is_packed_for_the_viewer_with_effective_settings()
    {
        var result = runtime.ParsePreview(new NcEnginePreviewRequest(
            "G18 G21 G99\nT0101\nG50 S2000\nG96 S150 M03\nG0 X50. Z2.\nG1 Z-20. F0.2\nM30\n",
            Settings: new NcEngineSettings(G30X: null, G30Z: 80, InitialVariables: "#500=1")));

        Assert.Equal("lathe", result.Summary.Kind);
        Assert.Equal(250d, result.Summary.Settings.G30X);
        Assert.Equal(80d, result.Summary.Settings.G30Z);
        Assert.Equal("#500=1", result.Summary.Settings.InitialVariables);
        Assert.StartsWith("{", result.Packed, StringComparison.Ordinal);
        Assert.Contains("cnc-model-columns-1", result.Packed, StringComparison.Ordinal);
    }

    [Fact]
    public void Inferred_tool_table_can_be_edited_and_manual_values_are_locked()
    {
        var text = "G18 G21 G99\nT0101 (OD TURN R0.8)\nG0 X50. Z2.\nG1 Z-20. F0.2\nT0202 (DRILL D10)\nM30\n";
        var inferred = runtime.InferToolTable(text, "O1500.nc", "auto", null, null);
        var editable = inferred.Editable;
        Assert.Equal("lathe", editable.GetProperty("machineType").GetString());
        Assert.Equal(2, editable.GetProperty("tools").GetArrayLength());

        var tools = editable.GetProperty("tools").EnumerateArray().Select(tool => new
        {
            number = tool.GetProperty("number").GetInt32(),
            cornerRadius = 0.4,
            tip = 3,
            type = tool.GetProperty("type").GetString(),
            hand = "right",
            description = "Edited"
        }).ToArray();
        var edited = System.Text.Json.JsonSerializer.SerializeToElement(new { name = "Edited table", units = "mm", tools });
        var saved = runtime.SaveToolTable(text, "O1500.nc", "auto", null, inferred.Table, edited);

        Assert.Contains("Edited table", saved.Xml, StringComparison.Ordinal);
        Assert.All(saved.Editable.GetProperty("tools").EnumerateArray(),
            tool => Assert.Equal(0.4, tool.GetProperty("cornerRadius").GetDouble()));
    }

    [Fact]
    public void Released_tool_table_descriptions_are_read_with_the_comment_heuristics()
    {
        // The Operation's tool table replaces the program's comments: the program says nothing about T1.
        var mill = "%\nO1500\nG21\nT1 M06\nG0 X0 Y0\nM30\n%\n";
        var tools = runtime.InferToolsFromDescriptions(mill,
        [
            new(1, "FLAT END MILL D12"),
            new(2, "DRILL 8.5MM"),
            new(3, "BALL_D6_R3_L=75"),
            new(4, ""),
            // The shop's CAM names: the diameter follows the tool words; an angle is not a diameter.
            new(5, "FIN_12_AROH_L=105"),
            new(6, "MERASEK 10 X 22"),
            new(7, "CHAMFER 45")
        ], "haas-vf-3ss", "HAAS_NGC");

        Assert.Equal([1, 2, 3, 4, 5, 6, 7], tools.Select(tool => tool.Number));
        Assert.Equal(["end-mill", "drill", "ball-mill", "other", "end-mill", "end-mill", "chamfer-mill"], tools.Select(tool => tool.Type));
        Assert.Equal([12, 8.5, 6, null, 12, 10, null], tools.Select(tool => tool.Diameter));
        Assert.Equal(3, tools[2].CornerRadius);
        Assert.Equal(75, tools[2].Length);
        Assert.Equal(105, tools[4].Length);
        Assert.Equal("BALL_D6_R3_L=75", tools[2].Description);

        var lathe = "G18 G21 G99\nT0101\nG0 X50. Z2.\nM30\n";
        var turning = runtime.InferToolsFromDescriptions(lathe,
        [
            new(1, "EXT TURN R0.8 TIP 3"),
            new(2, "DRILL D10")
        ], "haas-st-25y", "HAAS_NGC");

        Assert.Equal("external-cutter", turning[0].Type);
        Assert.Equal(0.8, turning[0].CornerRadius);
        Assert.Equal(3, turning[0].Tip);
        Assert.Equal("drill", turning[1].Type);
        Assert.Equal(10, turning[1].Diameter);
    }

    [Fact]
    public void Placeholder_and_print_lines_are_neutralized_without_moving_rows()
    {
        var text = "O1\n(PART: [[MEIMAD:PART_NAME]])\n[[MEIMAD:VERIFICATION_HOOK]]\nDPRNT[[[MEIMAD:PART_NAME]]]\nPUT 'X'\nWRITE C\nG0 X0\n";

        var prepared = NcPlaceholderText.ForEngine(text).Split('\n');

        Assert.Equal(text.Split('\n').Length, prepared.Length);
        Assert.Equal("(PART: MEIMAD-PART_NAME)", prepared[1]);
        Assert.Equal("(MEIMAD VERIFICATION_HOOK)", prepared[2]);
        Assert.Equal("(DPRNT[MEIMAD-PART_NAME])", prepared[3]);
        Assert.Equal("(PUT 'X')", prepared[4]);
        Assert.Equal("(WRITE C)", prepared[5]);
        Assert.Equal("G0 X0", prepared[6]);
    }

    [Fact]
    public void Nc_text_files_keep_their_line_ending_bom_and_eight_bit_characters()
    {
        var bom = NcTextFile.Decode([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("G0 X0\r\nM30\r\n")]);
        Assert.True(bom.HasBom);
        Assert.Equal("\r\n", bom.LineEnding);
        Assert.Equal("G0 X0\nM30\n", bom.Text);
        Assert.Equal([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("G0 X0\r\nM30\r\n")],
            NcTextFile.Encode(bom.Text, bom.LineEnding, bom.HasBom, bom.EncodingName));

        var latin = NcTextFile.Decode([.. Encoding.ASCII.GetBytes("(DIA "), 0xD8, .. Encoding.ASCII.GetBytes("10)\n")]);
        Assert.Equal("Latin-1", latin.EncodingName);
        Assert.Equal("(DIA Ø10)\n", latin.Text);
        Assert.Equal("\n", latin.LineEnding);

        Assert.Throws<InvalidDataException>(() => NcTextFile.Decode([0x47, 0x00, 0x30, 0x00]));
    }

    public void Dispose()
    {
        runtime.Dispose();
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }
}
