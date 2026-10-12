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

        var threeByThree = UcsMatrix.Compose([0, 0, 1, 0, 1, 0, -1, 0, 0], [80, 0, 40]);
        Assert.Equal(rowMajor, threeByThree!.Value.Matrix);
        Assert.Equal(rowMajor, UcsMatrix.Compose(rowMajor.Take(12).ToArray(), null)!.Value.Matrix);
        Assert.Equal(0, UcsMatrix.Compose([1, 0, 0, 0, 1, 0, 0, 0, 1], null)!.Value.Matrix[3]);
        Assert.Null(UcsMatrix.Compose([1, 2, 3], null));
        Assert.Null(UcsMatrix.Compose(null, [1, 2, 3]));
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
