using Meimad.CimatronExporter;

namespace Meimad.Planner.Server.Tests.SimulationPackages;

/// <summary>
/// The pieces of the COM adapter that read Cimatron 2026.0's answers without Cimatron: the process-manager XML
/// (procedures, UCS per procedure, operation), the UCS matrix composed from IModelTransformation's Matrix and Vector,
/// the Job Manager export folder, the stock STL beside the document, and the binder turning a number into a Cimatron
/// enum value (NcUcsType for ExportProceduresRemainingStock).
/// </summary>
public sealed class CimatronAccessorTests
{
    [Fact]
    public void Cimatron_cutters_and_holders_csv_give_sizes_by_magazine_number()
    {
        var folder = Directory.CreateTempSubdirectory("meimad-tool-csv-").FullName;
        try
        {
            var cutters = Path.Combine(folder, "cutters.csv");
            var holders = Path.Combine(folder, "holders.csv");
            CimatronToolCsvSamples.Write(cutters, holders);
            var tools = CimatronToolCsv.Read(cutters, holders).ToDictionary(tool => tool.Number);

            Assert.Equal([3, 29, 40], tools.Keys.Order());
            var flat = tools[29];
            Assert.Equal(("MERASEK_16", "flat", 16.0, 20.0, 52.0), (flat.Name, flat.Type, flat.DiameterMm!.Value, flat.FluteLengthMm!.Value, flat.GaugeLengthMm!.Value));
            Assert.Null(flat.CornerRadiusMm); // 0 means none
            Assert.Equal(("HOLDER-50_NEW", 50.0, 65.0), (flat.HolderName, flat.HolderDiameterMm!.Value, flat.HolderLengthMm!.Value));

            // A tapered cutter keeps its angle; the holder is three stages (the spindle stage after them does not count).
            var taper = tools[40];
            Assert.Equal(("taper", 45.0), (taper.Type, taper.TaperAngleDeg!.Value));
            Assert.Equal((63.0, 99.5), (taper.HolderDiameterMm!.Value, taper.HolderLengthMm!.Value));

            var ball = tools[3];
            Assert.Equal(("ball", 3.0), (ball.Type, ball.CornerRadiusMm!.Value));
            Assert.Null(ball.HolderDiameterMm); // holder not in holders.csv

            // An inch file is converted to millimetres.
            File.WriteAllText(cutters, CimatronToolCsvSamples.Cutters().Replace("|Units:|mm|Holders", "|Units:|inch|Holders"), System.Text.Encoding.Unicode);
            Assert.Equal(16 * 25.4, CimatronToolCsv.Read(cutters, null).Single(tool => tool.Number == 29).DiameterMm!.Value, 6);

            // The fake's plain CSV is not Cimatron's format: nothing is read.
            File.WriteAllText(cutters, "T,Name,Diameter\r\n1,D10 FLAT,10\r\n");
            Assert.Empty(CimatronToolCsv.Read(cutters, null));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Several_posted_programs_are_one_main_program_only_when_it_calls_the_others()
    {
        var folder = Directory.CreateTempSubdirectory("meimad-nc-set-").FullName;
        try
        {
            string Write(string name, string text)
            {
                var path = Path.Combine(folder, name);
                File.WriteAllText(path, text);
                return path;
            }

            // Mazak EIA: 6000 calls 5999 by number; 5999 calls a named subprogram.
            var main = NcProgramSet.Read(Write("6000.EIA", "%\nO6000(MAIN)\nT1M6\nM98 P5999\nM98 P9013\nT0\nM30\n%\n"));
            var sub = NcProgramSet.Read(Write("5999.EIA", "%\nO5999\nT2M6\nG65 P<CORNER>\nM99\n%\n"));
            var named = NcProgramSet.Read(Write("CORNER.EIA", "%\nO<CORNER>\nG1X0\nM99\n%\n"));
            Assert.Equal("6000", main.Number);
            Assert.Equal(["5999", "9013"], main.Calls.Order());
            Assert.Equal([1], main.Tools);
            Assert.Same(main, NcProgramSet.MainProgram([sub, named, main]));
            Assert.Contains("calls 5999, 9013", NcProgramSet.Describe(main, folder));
            // 9013 is a machine-resident program (the Meimad Mazak post of 2026-10-12).
            Assert.Equal(["9013"], NcProgramSet.CallsOutside([main, sub, named]));

            // Two programs that do not call each other, and a file the main program does not reach: the user chooses.
            var other = NcProgramSet.Read(Write("6001.EIA", "%\nO6001\nT3M6\nM30\n%\n"));
            Assert.Null(NcProgramSet.MainProgram([main, other]));
            Assert.Null(NcProgramSet.MainProgram([main, sub, named, other]));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Process_manager_xml_gives_procedures_with_ids_names_ucs_and_operation()
    {
        const string xml = """
            <?xml version="1.0"?>
            <ProcessManager>
              <Setup Name="OP010" UID="3">
                <UCS Name="UCS_OP010"/>
                <Procedures>
                  <Procedure UID="17" Name="ROUGH_POCKET" UCS="UCS_OP010" Status="Calculated"/>
                  <Procedure UID="18" Name="FINISH_WALLS" Calculated="false">
                    <Parameter Name="Procedure UCS" Value="UCS_OP010"/>
                  </Procedure>
                  <Procedure UID="17" Name="duplicate id is ignored"/>
                  <Procedure Name="no id is ignored"/>
                </Procedures>
              </Setup>
              <Item Type="NcProcedure" Id="25" Name="DRILL">
                <ProcUcs>UCS_OP020</ProcUcs>
              </Item>
            </ProcessManager>
            """;
        var data = ProcessManagerXml.Parse(xml);
        Assert.Equal([17, 18, 25], data.Procedures.Select(item => item.Id));
        Assert.Equal(new CimatronProcedure(17, "ROUGH_POCKET", "UCS_OP010", true, "OP010"), data.Procedures[0]);
        Assert.Equal(new CimatronProcedure(18, "FINISH_WALLS", "UCS_OP010", false, "OP010"), data.Procedures[1]);
        Assert.Equal(new CimatronProcedure(25, "DRILL", "UCS_OP020", true, null), data.Procedures[2]);
        Assert.Equal(["UCS_OP010", "UCS_OP020"], data.UcsNames);
        Assert.Empty(ProcessManagerXml.Parse("not xml").Procedures);
    }

    [Fact]
    public void Cimatron_2026_full_report_gives_toolpath_procedures_with_their_ucs_and_the_stock_procedure()
    {
        // The layout Cimatron 2026.0 SP3P1 wrote at Meimad on 2026-10-12 (GetProcessManagerAsXML), trimmed.
        const string xml = """
            <?xml version="1.0"?>
            <NCFullReport><File_Units Value="mm" ID="30004"/>
              <Setup Name="NC_Setup" ID="1"><SetupParameters><UCS Value="MODEL-1"/></SetupParameters>
                <SetupFolder>
                  <Procedure Name="Target Part_2" ID="2"><ProcedureParameters>
                    <Procedure_Comment Value="No Text" ID="30008"/>
                    <Procedure_Status Value="Calculation succeeded" ID="30009"/></ProcedureParameters></Procedure>
                  <Procedure Name="Stock By Contours_3" ID="3"><ProcedureParameters>
                    <Procedure_Status Value="Calculation succeeded" ID="30009"/>
                    <Stock_Type Value="Contours" ID="381"/></ProcedureParameters></Procedure>
                  <ToolPath Name="TP_MODEL" ID="4">
                    <ToolpathParameters><ToolPath_UCS Value="MODEL" ID="30101"/></ToolpathParameters>
                    <ToolpathFolder>
                      <Procedure Name="2.5 Axes-Profile Open Contour_6" ID="6">
                        <ProcedureParameters>
                          <Procedure_Status Value="Calculation succeeded" ID="30009"/>
                          <Procedure_Unique_Number Value="6" ID="30069"/></ProcedureParameters>
                        <GridParameters><Motion_Parameters Value="" ID="-1"><Clearance___UCS Value="" ID="-1">
                          <UCS_Name Value="FAZA----11111" ID="3217"/><Create_UCS Value="Access" ID="21118"/>
                        </Clearance___UCS></Motion_Parameters></GridParameters>
                      </Procedure>
                      <Procedure Name="Drill 5x_59" ID="59">
                        <ProcedureParameters><Procedure_Status Value="Not calculated" ID="30009"/></ProcedureParameters>
                      </Procedure>
                    </ToolpathFolder>
                  </ToolPath>
                </SetupFolder>
              </Setup>
            </NCFullReport>
            """;
        var data = ProcessManagerXml.Parse(xml);
        Assert.Equal([2, 3, 6, 59], data.Procedures.Select(item => item.Id));
        Assert.Equal(new CimatronProcedure(2, "Target Part_2", null, false, "NC_Setup"), data.Procedures[0]);
        Assert.Equal(new CimatronProcedure(3, "Stock By Contours_3", null, false, "NC_Setup"), data.Procedures[1]);
        Assert.Equal(new CimatronProcedure(6, "2.5 Axes-Profile Open Contour_6", "FAZA----11111", true, "TP_MODEL"), data.Procedures[2]);
        // No UCS of its own: the toolpath's UCS; not calculated: no toolpath.
        Assert.Equal(new CimatronProcedure(59, "Drill 5x_59", "MODEL", false, "TP_MODEL"), data.Procedures[3]);
        Assert.Equal([3], data.StockProcedures);
        Assert.Equal([2, 3], data.NonMachining);
        Assert.Equal([new CimatronToolpath(4, "TP_MODEL", "NC_Setup", 3)], data.Toolpaths);
        Assert.Equal([6, 59], data.Machining.Select(item => item.Id));
        Assert.Equal(["MODEL-1", "MODEL", "FAZA----11111"], data.UcsNames);
    }

    [Fact]
    public void Ucs_matrix_is_composed_from_what_cimatron_returns()
    {
        var rowMajor = new double[] { 0, 0, 1, 80, 0, 1, 0, 0, -1, 0, 0, 40, 0, 0, 0, 1 };
        var composed = UcsMatrix.Compose(rowMajor, null);
        Assert.Equal(rowMajor, composed!.Value.Matrix);
        Assert.Contains("row-major", composed.Value.Layout);

        // Translation in the last row (column-major storage) is transposed to the row-major convention.
        var columnMajor = new double[] { 0, 0, -1, 0, 0, 1, 0, 0, 1, 0, 0, 0, 80, 0, 40, 1 };
        var transposed = UcsMatrix.Compose(columnMajor, null);
        Assert.Equal(rowMajor, transposed!.Value.Matrix);
        Assert.Contains("transposed", transposed.Value.Layout);

        var threeByThree = UcsMatrix.Compose([0, 0, -1, 0, 1, 0, 1, 0, 0], [80, 0, 40]);
        Assert.Equal(rowMajor, threeByThree!.Value.Matrix);
        Assert.Contains("axes as rows", threeByThree.Value.Layout);

        // Meimad's "---90" (probe of 2026-10-12): origin on the stock face x = -69.85, so the UCS Z axis is -X.
        var minus90 = UcsMatrix.Compose([0, 1, 0, 0, 0, -1, -1, 0, 0], [-69.85, 0, 17.9171])!.Value.Matrix;
        Assert.Equal([-1.0, 0, 0], new[] { minus90[2], minus90[6], minus90[10] });
        Assert.Equal(rowMajor, UcsMatrix.Compose(rowMajor.Take(12).ToArray(), null)!.Value.Matrix);
        Assert.Equal(0, UcsMatrix.Compose([1, 0, 0, 0, 1, 0, 0, 0, 1], null)!.Value.Matrix[3]);
        Assert.Null(UcsMatrix.Compose([1, 2, 3], null));
        Assert.Null(UcsMatrix.Compose(null, [1, 2, 3]));
    }

    [Fact]
    public void Ucs_layout_is_decided_from_translated_points()
    {
        // UCS11_3 of the Meimad probe (2026-10-12): a tilted, non-symmetric rotation.
        double[] composed = [-0.00771125, -0.99997, 0, 55.9491, 0.101128, -0.000779848, -0.994873, 60, 0.994844, -0.00767172, 0.101131, 96.5154, 0, 0, 0, 1];
        double[] origin = [55.9491, 60, 96.5154];
        double[] Point(int column) => [origin[0] + composed[column], origin[1] + composed[4 + column], origin[2] + composed[8 + column]];
        Assert.StartsWith("CONFIRMED", UcsLayoutCheck.Compare(composed, origin, Point(0), Point(1), Point(2)));

        double[] Transposed(int row) => [origin[0] + composed[row * 4], origin[1] + composed[row * 4 + 1], origin[2] + composed[row * 4 + 2]];
        Assert.Contains("TRANSPOSED", UcsLayoutCheck.Compare(composed, origin, Transposed(0), Transposed(1), Transposed(2)));

        double[] identity = [1, 0, 0, 5, 0, 1, 0, 6, 0, 0, 1, 7, 0, 0, 0, 1];
        Assert.Contains("symmetric", UcsLayoutCheck.Compare(identity, [5, 6, 7], [6, 6, 7], [5, 7, 7], [5, 6, 8]));
        Assert.StartsWith("matches neither", UcsLayoutCheck.Compare(identity, [0, 0, 0], [1, 0, 0], [0, 1, 0], [0, 0, 1]));
    }

    [Fact]
    public void Stl_bounds_are_read_from_a_binary_stl()
    {
        var file = Path.Combine(Path.GetTempPath(), $"meimad-stl-bounds-{Guid.NewGuid():N}.stl");
        try
        {
            using (var writer = new BinaryWriter(File.Create(file)))
            {
                writer.Write(new byte[80]);
                writer.Write(2u);
                float[][] triangles = [[-66, -63.5f, 0, 66, -63.5f, 0, 66, 63.7f, 0], [-66, -63.5f, 99.5f, 66, 63.7f, 99.5f, -66, 63.7f, 99.5f]];
                foreach (var triangle in triangles)
                {
                    writer.Write(new byte[12]);
                    foreach (var value in triangle) writer.Write(value);
                    writer.Write((ushort)0);
                }
            }

            var bounds = StlBounds.Read(file)!.Value;
            Assert.Equal(2, bounds.Triangles);
            Assert.Equal([-66, -63.5, 0], bounds.Min);
            Assert.Equal(99.5, bounds.Max[2]);
            File.WriteAllText(file, "solid ascii");
            Assert.Null(StlBounds.Read(file));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Jobs_export_folder_is_normalized_to_one_xml_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "MeimadPlanner.CimatronAccessor.Tests", Guid.NewGuid().ToString("N"));
        var metadata = Path.Combine(root, "metadata");
        Directory.CreateDirectory(metadata);
        try
        {
            var xmlPath = Path.Combine(metadata, "jobs.xml");
            var export = Path.Combine(metadata, "jobs-export");

            // One file in the folder becomes jobs.xml.
            Directory.CreateDirectory(export);
            File.WriteAllText(Path.Combine(export, "JobManager.xml"), "<jobs/>");
            Assert.True(JobsExportFiles.Normalize(export, xmlPath));
            Assert.Equal("<jobs/>", File.ReadAllText(xmlPath));
            Assert.False(Directory.Exists(export));

            // Several files stay under metadata/jobs; jobs.xml is not claimed.
            File.Delete(xmlPath);
            Directory.CreateDirectory(export);
            File.WriteAllText(Path.Combine(export, "job1.xml"), "<job/>");
            File.WriteAllText(Path.Combine(export, "job2.xml"), "<job/>");
            Assert.False(JobsExportFiles.Normalize(export, xmlPath));
            Assert.Equal(2, Directory.GetFiles(Path.Combine(metadata, "jobs")).Length);

            // A file written at the folder path itself is the XML.
            File.WriteAllText(export, "<jobs/>");
            Assert.True(JobsExportFiles.Normalize(export, xmlPath));
            Assert.True(File.Exists(xmlPath));

            // Nothing written: no XML.
            File.Delete(xmlPath);
            Directory.CreateDirectory(export);
            Assert.False(JobsExportFiles.Normalize(export, xmlPath));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Stock_files_beside_the_document_are_found_in_its_folder_and_nc_subfolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "MeimadPlanner.CimatronAccessor.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "NC"));
        try
        {
            Assert.Empty(StockFileDiscovery.Find(null));
            Assert.Empty(StockFileDiscovery.Find(Path.Combine(root, "missing", "X.elt")));
            Assert.Empty(StockFileDiscovery.Find(Path.Combine(root, "X.elt")));
            File.WriteAllText(Path.Combine(root, "NC", "STOCK_OP1.stl"), "");
            File.WriteAllText(Path.Combine(root, "NC", "part.stl"), "");
            File.WriteAllText(Path.Combine(root, "stock_op2.STL"), "");
            Assert.Equal(
                [Path.Combine(root, "NC", "STOCK_OP1.stl"), Path.Combine(root, "stock_op2.STL")],
                StockFileDiscovery.Find(Path.Combine(root, "X.elt")));

            // Meimad's layout: the document in NC\<part>\, the stock export in NC\.
            Directory.CreateDirectory(Path.Combine(root, "NC", "PART-501"));
            Assert.Equal([Path.Combine(root, "NC", "STOCK_OP1.stl")], StockFileDiscovery.Find(Path.Combine(root, "NC", "PART-501", "PART_NC2.elt")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    public enum UcsType
    {
        Setup = 0,
        Procedure = 1,
        Active = 2,
        Model = 3
    }

    public interface IRemainingStock
    {
        void ExportProceduresRemainingStock(int procedure, UcsType ucsType, string file, out bool created);
    }

    private sealed class Model : IRemainingStock
    {
        public UcsType? Frame { get; private set; }

        public void ExportProceduresRemainingStock(int procedure, UcsType ucsType, string file, out bool created)
        {
            Frame = ucsType;
            created = true;
        }
    }

    [Fact]
    public void Binder_turns_the_nc_ucs_type_number_into_the_enum_value()
    {
        var binder = new CimatronBinder([typeof(IRemainingStock)]);
        var model = new Model();
        Assert.True(binder.TryInvoke(model, "ExportProceduresRemainingStock", [9, CimatronComApi.Members.ModelUcsType, @"C:\stock.stl"], out _, out var outputs));
        Assert.Equal(UcsType.Model, model.Frame);
        Assert.Equal(true, outputs[3]);
        Assert.Equal(["IRemainingStock"], binder.Interfaces(model));
        Assert.Empty(binder.Interfaces(new object()));
    }
}
