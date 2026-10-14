using System.IO.Compression;
using System.Text;
using Meimad.Planner.Server.Application.LocalAi;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Meimad.Planner.Server.Tests.ProductionPackages.ProductionPackageContextTests.Fixture;
namespace Meimad.Planner.Server.Tests.LocalAi;

public sealed class LocalCorpusFormatTests
{
    [Fact]
    public async Task Csv_workbooks_and_step_model_metadata_are_indexed_as_labelled_text()
    {
        await using var fixture = await Fixture.CreateAsync();
        var root = Path.Combine(Path.GetTempPath(), "MeimadCorpus", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "materials.csv"), "Part,Material\nPN-CSV-1,Inconel 718 חומר\n", new UTF8Encoding(false));
            await File.WriteAllBytesAsync(Path.Combine(root, "tools.xlsx"), Workbook());
            var step = Path.Combine(root, "ASSY-100.stp");
            await File.WriteAllTextAsync(step, Step(), new UTF8Encoding(false));
            await File.WriteAllTextAsync(Path.Combine(root, "notstep.stp"), "hello; this is not a model");
            await File.WriteAllBytesAsync(Path.Combine(root, "broken.xlsx"), [1, 2, 3]);
            await File.WriteAllBytesAsync(Path.Combine(root, "codepage.csv"), [0xF9, 0xEC, 0xE5, 0xED, 0x2C, 0x31]); // Windows-1255, not UTF-8
            var service = fixture.App.Services.GetRequiredService<LocalCorpusService>();
            var settings = await service.UpdateAsync(new(0, [root]), "admin", default);
            var result = await service.IndexAsync(settings.Version, default);
            Assert.Equal(3, result.IndexedFiles); Assert.Equal(3, result.SkippedFiles);

            var csv = Assert.Single(await service.SearchAsync("Inconel 718", default));
            Assert.Equal(("materials.csv", "CSV"), (csv.Title, csv.Extraction));

            var workbook = await ContentAsync(fixture, "tools.xlsx");
            Assert.Contains("[Sheet: Tools]", workbook, StringComparison.Ordinal);
            Assert.Contains("Row 1: A = Tool list 2026", workbook, StringComparison.Ordinal);
            Assert.Contains("Row 2: (headers) Tool | Diameter | Received | Coolant", workbook, StringComparison.Ordinal);
            // Each row names its headers, dates read as dates, and binary doubles read as typed.
            Assert.Contains("Row 3: Tool = EM-12; Diameter = 12; Received = 2026-08-03; Coolant = 0.3; E = חומר", workbook, StringComparison.Ordinal);
            Assert.Contains("[Sheet: Lookup]", workbook, StringComparison.Ordinal);
            var hidden = Assert.Single(await service.SearchAsync("HIDDEN-VALUE", default));
            Assert.Equal("XLSX", hidden.Extraction);
            Assert.Contains("Excel workbook", hidden.MatchReason, StringComparison.Ordinal);

            var model = await ContentAsync(fixture, "ASSY-100.stp");
            foreach (var expected in new[]
            {
                "Geometry and dimensions are not indexed", "File name: ASSY-100.stp", "Description: CATIA V5 STEP Exchange",
                "Author: Dana", "Organization: Meimad", "Originating system: CATIA Version 5 Release 2022", "Exporting software: ST-DEVELOPER",
                "Schema: AUTOMOTIVE_DESIGN (AP214)", "Length unit: millimetre", "Solid bodies: 1",
                "- ASSY-100 | name: Bracket assembly | version: B", "- PN-STEP-7 | name: Pin | description: Pin שלום it's | version: A",
                "- ASSY-100 contains PN-STEP-7 (x2)", "- material: Ti-6Al-4V", "- finish: anodize; after machining",
            })
                Assert.Contains(expected, model, StringComparison.Ordinal);
            Assert.DoesNotContain("#100000", model, StringComparison.Ordinal);
            Assert.DoesNotContain("Created: none", model, StringComparison.Ordinal);
            var match = Assert.Single(await service.SearchAsync("PN-STEP-7", default));
            Assert.Equal("STEP", match.Extraction);
            Assert.Contains("no dimensions", match.MatchReason, StringComparison.Ordinal);
            // Workbook and STEP text is cached by file hash like PDF text; CSV is read directly.
            Assert.Equal(2L, await fixture.ScalarAsync("SELECT COUNT(*) FROM local_ai_extraction_cache"));
            Assert.Equal(1L, await fixture.ScalarAsync("SELECT COUNT(*) FROM local_ai_extraction_cache WHERE extraction='STEP'"));

            // A changed model is hidden until reindexed, exactly like a changed text file.
            await File.AppendAllTextAsync(step, "/* edited */");
            Assert.Empty(await service.SearchAsync("PN-STEP-7", default));
            await service.IndexAsync(settings.Version, default);
            Assert.Single(await service.SearchAsync("PN-STEP-7", default));
            Assert.Equal(2L, await fixture.ScalarAsync("SELECT COUNT(*) FROM local_ai_extraction_cache"));
        }
        finally
        {
            Assert.StartsWith(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MeimadCorpus")) + Path.DirectorySeparatorChar, Path.GetFullPath(root));
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Step_string_escapes_decode_to_unicode()
    {
        Assert.Equal("שלום", CorpusStepReader.Decode(@"\X2\05E905DC05D505DD\X0\"));
        Assert.Equal("é and \\ and 😀", CorpusStepReader.Decode(@"\X\E9 and \\ and \X4\0001F600\X0\"));
        Assert.Equal("é", CorpusStepReader.Decode(@"\S\i"));
    }

    [Fact]
    public async Task Cmm_programs_rtf_reports_and_excel_97_workbooks_are_indexed_as_labelled_text()
    {
        await using var fixture = await Fixture.CreateAsync();
        var root = Path.Combine(Path.GetTempPath(), "MeimadCorpus", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "inspection.dmi"), "F(HOLE1)=FEAT/CIRCLE,INNER,CART,0,0,0,0,0,1,15.913\nT(D0)=TOL/DIAM,-0.053,-0.028\n");
            await File.WriteAllTextAsync(Path.Combine(root, "position.dmi"), "T(P0)=TOL/POS,2D,0.020,DAT(C)\n", Encoding.Unicode); // UTF-16 with BOM
            await File.WriteAllTextAsync(Path.Combine(root, "report.rtf"), """
                {\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0\fnil\fcharset0 Courier New;}}{\colortbl ;\red0\green0\blue255;}
                {\*\generator Riched20 10.0;}\viewkind4\uc1\pard\f0\fs16 HOLE-D1 ACTUAL 15.880 NOMINAL 15.913\par
                Caf\'e9 UESC1513?UESC1500?UESC1493?UESC1501? done\par
                \trowd\cellx1000\cellx2000 A1\cell B1\cell\row
                {\header HEADER-TEXT\par}
                }
                """.Replace("UESC", "\\" + "u", StringComparison.Ordinal)); // RTF Unicode escapes, spelled out of C# escape reach
            // CMM software on Hebrew Windows: raw code page 1255 bytes under a 1252 declaration.
            await File.WriteAllBytesAsync(Path.Combine(root, "cmm.rtf"), [.. Encoding.ASCII.GetBytes(@"{\rtf1\ansi\ansicpg1252\pard CMM-DATE 23-"),
                0xE0, 0xE5, 0xE2, 0xE5, 0xF1, 0xE8, .. Encoding.ASCII.GetBytes(@"-2017\par}")]);
            File.Copy(Path.Combine(RepositoryRoot(), "tests", "fixtures", "local-ai", "corpus-sample.xls"), Path.Combine(root, "inspection.xls"));
            await File.WriteAllBytesAsync(Path.Combine(root, "broken.xls"), [1, 2, 3]);
            await File.WriteAllTextAsync(Path.Combine(root, "plain.rtf"), "not an rtf document");
            var service = fixture.App.Services.GetRequiredService<LocalCorpusService>();
            var settings = await service.UpdateAsync(new(0, [root]), "admin", default);
            var result = await service.IndexAsync(settings.Version, default);
            Assert.Equal(5, result.IndexedFiles); Assert.Equal(2, result.SkippedFiles);

            var program = Assert.Single(await service.SearchAsync("TOL/DIAM", default));
            Assert.Equal(("inspection.dmi", "DMIS"), (program.Title, program.Extraction));
            Assert.Contains("CMM inspection program", program.MatchReason, StringComparison.Ordinal);
            Assert.Equal("position.dmi", Assert.Single(await service.SearchAsync("DAT(C)", default)).Title);

            var report = await ContentAsync(fixture, "report.rtf");
            Assert.Contains("HOLE-D1 ACTUAL 15.880 NOMINAL 15.913\n", report, StringComparison.Ordinal);
            Assert.Contains("Café שלום done\n", report, StringComparison.Ordinal);
            Assert.Contains("A1 | B1 |", report, StringComparison.Ordinal);
            foreach (var dropped in new[] { "Courier", "Riched20", "HEADER-TEXT", "rtf1", "\\" })
                Assert.DoesNotContain(dropped, report, StringComparison.Ordinal);
            Assert.Contains("CMM-DATE 23-אוגוסט-2017", await ContentAsync(fixture, "cmm.rtf"), StringComparison.Ordinal);
            Assert.Equal("RTF", Assert.Single(await service.SearchAsync("HOLE-D1", default)).Extraction);

            var workbook = await ContentAsync(fixture, "inspection.xls");
            Assert.Contains("[Sheet: CMM]", workbook, StringComparison.Ordinal);
            Assert.Contains("Row 2: (headers) Feature | Nominal | Lower | Upper | Measured on", workbook, StringComparison.Ordinal);
            Assert.Contains("Row 3: Feature = HOLE-A; Nominal = 15.913; Lower = -0.053; Upper = -0.028; Measured on = 2017-08-24", workbook, StringComparison.Ordinal);
            Assert.Contains("Row 4: Feature = קדח; Nominal = 12.733", workbook, StringComparison.Ordinal);
            var hidden = Assert.Single(await service.SearchAsync("XLS-HIDDEN-NOTE", default));
            Assert.Equal(("inspection.xls", "XLS"), (hidden.Title, hidden.Extraction));
            Assert.Contains("Excel 97-2003", hidden.MatchReason, StringComparison.Ordinal);
            // Workbook and RTF text is cached by file hash; DMIS programs are plain text and read directly.
            Assert.Equal(3L, await fixture.ScalarAsync("SELECT COUNT(*) FROM local_ai_extraction_cache"));
        }
        finally
        {
            Assert.StartsWith(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MeimadCorpus")) + Path.DirectorySeparatorChar, Path.GetFullPath(root));
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Cimatron_documents_tool_sheets_and_export_packages_are_indexed_as_labelled_text()
    {
        await using var fixture = await Fixture.CreateAsync();
        var root = Path.Combine(Path.GetTempPath(), "MeimadCorpus", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(root, "30P-NC1.elt"), Elt());
            File.Copy(Path.Combine(root, "30P-NC1.elt"), Path.Combine(root, "30P-NC1.elt.bac")); // Cimatron backup: not indexed
            await File.WriteAllBytesAsync(Path.Combine(root, "old.elt"), [0x01, 0x02, 0x03, 0x04]); // not a document container
            var package = Path.Combine(root, "cimatron-export");
            Directory.CreateDirectory(Path.Combine(package, "tools"));
            Directory.CreateDirectory(Path.Combine(package, "metadata"));
            await File.WriteAllTextAsync(Path.Combine(package, "tools", "TP_MODEL.TOOLS.mht"), Mht(), Encoding.ASCII);
            await File.WriteAllTextAsync(Path.Combine(package, "tools", "tool-table.json"), """
                {"schemaVersion":1,"units":"mm","tools":[{"number":29,"toolIdentifier":"T29","name":"MERASEK_16","type":"flat","diameterMm":16,
                 "fluteLengthMm":20,"gaugeLengthMm":52,"holder":{"name":"HOLDER-50_NEW"}}]}
                """);
            await File.WriteAllTextAsync(Path.Combine(package, "metadata", "jobs.xml"), """
                <?xml version="1.0" encoding="UTF-16"?>
                <JobManager><Job Name="" ID="1"><JobParamters><Parameter MachineTime="00:01:11"/></JobParamters>
                <Post Name="FANUC_4X"><RefernceUcs Name="MODEL"/><DateAndTime Value="13-07-2017 14:34:15"/>
                <GCodeFiles><File Name="C:\WWD\nc1.DIR\FANUC_4X\TP_MODEL_0.CNC"/></GCodeFiles></Post></Job></JobManager>
                """, Encoding.Unicode);
            await File.WriteAllTextAsync(Path.Combine(package, "manifest.json"), """
                {"schemaVersion":1,"part":{"number":"30P450172200-002","name":"FITTING"},
                 "source":{"application":"Cimatron","project":"30P-NC1","documentPath":"J:\\DPD\\30P\\30P-NC1.elt","setup":"M_MODE",
                   "ncFileName":"6000.EIA","exportedAtUtc":"2026-10-12T14:27:33Z","host":"OLEG-PC","user":"oleg",
                   "operations":[{"kind":"toolpath","id":4,"name":"TP_MODEL","toolpath":"TP_MODEL"}]},
                 "exporter":{"version":"0.3.1","cimatronVersion":"2026,0301"},
                 "operation":{"number":"010","name":"TP_MODEL","procedureName":"2.5 Axes-Profile Open Contour_5"},
                 "machine":{"postprocessor":"Mazak_5x_Variaxis_i-500_TTCA_RTCP"},
                 "coordinateSystem":{"name":"MODEL","workOffset":"G54","originMm":{"x":0,"y":0,"z":0},"rotationDeg":{"x":0,"y":0,"z":90}},
                 "files":{"stock":[{"kind":"initial","path":"geometry/stock.stl"}],"fixtures":[{"path":"geometry/vise.stl"}],
                   "toolTable":{"path":"tools/tool-table.json"},"jobsXml":{"path":"metadata/jobs.xml"},"traversal":{"path":"../../secret.json"}}}
                """);
            // Another folder's manifest.json is not a Cimatron package and stays unsupported.
            await File.WriteAllTextAsync(Path.Combine(root, "manifest.json"), """{"name":"other"}""");

            var service = fixture.App.Services.GetRequiredService<LocalCorpusService>();
            var settings = await service.UpdateAsync(new(0, [root]), "admin", default);
            var result = await service.IndexAsync(settings.Version, default);
            Assert.Equal(3, result.IndexedFiles);

            var document = await ContentAsync(fixture, "30P-NC1.elt");
            foreach (var expected in new[]
            {
                "Cimatron document (native .elt)", "File name: 30P-NC1.elt", "Document type: NC (CAM program file)", "Units: mm",
                "Part size: 400.0 X 231.0 X 316.0 mm", "Created: 02-Jul-17 2:17:47 PM by DIMITRY on DIMITRY-PC (Cimatron build 13,0400)",
                "Last modified: 9/16/2026 12:23:55 PM by oleg on OLEG-PC", "Imported from: J:\\DPD\\30P\\30P-001.STP (STEP)",
                "NC setups: 3 (M_MODE, OP20 & side, unnamed)", "Toolpath blocks: 2",
            })
                Assert.Contains(expected, document, StringComparison.Ordinal);
            var match = Assert.Single(await service.SearchAsync("DIMITRY-PC", default));
            Assert.Equal(("30P-NC1.elt", "CIMATRON"), (match.Title, match.Extraction));
            Assert.Contains("Cimatron file", match.MatchReason, StringComparison.Ordinal);

            var sheet = await ContentAsync(fixture, "cimatron-export/tools/TP_MODEL.TOOLS.mht");
            Assert.Contains("T29 | MERASEK_16 | 16. | HOLDER-50_NEW", sheet, StringComparison.Ordinal);
            Assert.Contains("רשימת כלים", sheet, StringComparison.Ordinal);
            Assert.Contains("Number מספר | Name | Dia | Holder", sheet, StringComparison.Ordinal);
            Assert.DoesNotContain("Shuki", sheet, StringComparison.Ordinal);
            Assert.DoesNotContain("font-family", sheet, StringComparison.Ordinal);
            Assert.Equal("MHT", Assert.Single(await service.SearchAsync("HOLDER-50_NEW", default), hit => hit.Extraction == "MHT").Extraction);

            var summary = await ContentAsync(fixture, "cimatron-export/manifest.json");
            foreach (var expected in new[]
            {
                "Cimatron export package", "Part: 30P450172200-002 FITTING", "Cimatron file: J:\\DPD\\30P\\30P-NC1.elt", "Setup: M_MODE",
                "Exported: 2026-10-12T14:27:33Z by oleg on OLEG-PC", "Postprocessor: Mazak_5x_Variaxis_i-500_TTCA_RTCP",
                "Work CS: MODEL G54 origin X0 Y0 Z0 rotation X0 Y0 Z90", "- CAM operation: toolpath TP_MODEL (toolpath TP_MODEL)",
                "Stock file: geometry/stock.stl (initial)", "Fixture file: geometry/vise.stl",
                "- T29 | MERASEK_16 | flat | diameter 16 mm | flute 20 mm | gauge 52 mm | holder HOLDER-50_NEW",
                "- FANUC_4X | UCS MODEL | 13-07-2017 14:34:15 | machine time 00:01:11 | TP_MODEL_0.CNC",
            })
                Assert.Contains(expected, summary, StringComparison.Ordinal);
            Assert.Equal("CIMATRON", Assert.Single(await service.SearchAsync("Variaxis", default)).Extraction);
            // Each Cimatron text is cached by file hash like PDF text.
            Assert.Equal(3L, await fixture.ScalarAsync("SELECT COUNT(*) FROM local_ai_extraction_cache WHERE extraction IN ('CIMATRON','MHT')"));
        }
        finally
        {
            Assert.StartsWith(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MeimadCorpus")) + Path.DirectorySeparatorChar, Path.GetFullPath(root));
            Directory.Delete(root, true);
        }
    }

    private static byte[] Elt()
    {
        // .exl as Cimatron writes it: one XML document, the document's attribute list first, then a list per data block.
        static string List(params (string Id, string Value)[] values) =>
            "<Cim:Attr_list>" +
            string.Concat(values.Select(value => $"<Cim:Attr Id=\"{value.Id}\" Type=\"string\" User=\"System\" Access=\"r\">{value.Value}</Cim:Attr>")) +
            "<Cim:Attr Id=\"GRID_SIZE\" Type=\"int\" User=\"DS\" Access=\"no\"></Cim:Attr></Cim:Attr_list>";
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Entry(string name, byte[] bytes)
            {
                using var stream = zip.CreateEntry(name, CompressionLevel.NoCompression).Open();
                stream.Write(bytes);
            }
            Entry(".exl", Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<Cim:Doc xmlns:Cim=\"CimDdlShema.xml\">" +
                List(("CN_UNITS_OF_MEASURE", "mm"), ("ELITE_DOC_TYPE", "NC"), ("Cim_E_CREATED_BY", "DIMITRY"), ("Cim_E_CREATED_FROM", "DIMITRY-PC"),
                    ("Cim_E_CREATION_DATE", "02-Jul-17 2:17:47 PM"), ("Cim_E_CREATED_IN_PASS", "13,0400"),
                    ("DI::ORIG_FILENAME", @"J:\DPD\30P\30P-001.STP"), ("DI::ORIG_FILEFORMAT", "STEP"),
                    ("TMPL::Visible_Part_Size", "400.0 X 231.0 X 316.0"), ("Cim_E_MODIFIED_BY", "oleg"), ("Cim_E_MODIFIED_FROM", "OLEG-PC"),
                    ("Cim_E_MODIFICATION_DATE", "9/16/2026 12:23:55 PM")) +
                "<Cim:DB_list><Cim:DB_BINARY Id=\"ProgramDataBlock\" SubType=\"ProgramDataBlock\">" + List(("DATABLOCK_DATA_VERSION", "2")) + "</Cim:DB_BINARY>" +
                List(("ELITE_DOC_TYPE", "NC-Setup"), ("CN_TITLE", "M_MODE")) +
                List(("ELITE_DOC_TYPE", "NC-Setup"), ("CN_TITLE", "OP20 &amp; side")) +
                List(("ELITE_DOC_TYPE", "NC-Setup")) +
                List(("MotNumber", "83"), ("EndPoint", "114.85 52.5 140.")) +
                List(("MotNumber", "134")) + "</Cim:DB_list></Cim:Doc>"));
            Entry(".sab", [0x41, 0x43, 0x49, 0x53, 0x00, 0xFF]); // binary geometry: never read as text
        }
        return buffer.ToArray();
    }

    private static string Mht()
    {
        // Word "Single File Web Page" as Cimatron writes its tool sheet: quoted-printable, Hebrew as character references.
        var html = """
            <html xmlns:o="urn:schemas-microsoft-com:office:office"><head><meta charset="us-ascii"><title>Shuki</title>
            <style>p { font-family: Arial }</style><xml><o:DocumentProperties><o:Author>Shuki</o:Author></o:DocumentProperties></xml></head>
            <body><!--[if gte mso 9]><xml><w:WordDocument>Shuki</w:WordDocument></xml><![endif]-->
            <p>&#1512;&#1513;&#1497;&#1502;&#1514; &#1499;&#1500;&#1497;&#1501;</p>
            <table><tr><td><p>Number</p><p>&#1502;&#1505;&#1508;&#1512;</p></td><td>Name</td><td>Dia</td><td>Holder</td></tr>
            <tr><td><p class=MsoNormal>T29</p></td><td><p>MERASEK_16</p></td><td>16.</td><td>HOLDER-50_NEW</td></tr></table></body></html>
            """;
        var encoded = new StringBuilder();
        foreach (var line in html.Split('\n'))
        {
            var quoted = line.TrimEnd('\r').Replace("=", "=3D", StringComparison.Ordinal);
            while (quoted.Length > 70)
            {
                var cut = 70;
                while (quoted[cut - 1] == '=' || quoted[cut - 2] == '=') cut--;
                encoded.Append(quoted[..cut]).Append("=\r\n");
                quoted = quoted[cut..];
            }
            encoded.Append(quoted).Append("\r\n");
        }
        return "MIME-Version: 1.0\r\nContent-Type: multipart/related; boundary=\"----=_NextPart_01DB\"\r\n\r\nThis document is a Single File Web Page.\r\n\r\n" +
               "------=_NextPart_01DB\r\nContent-Location: file:///C:/T/Template_Tools.htm\r\nContent-Transfer-Encoding: quoted-printable\r\n" +
               "Content-Type: text/html; charset=\"us-ascii\"\r\n\r\n" + encoded +
               "\r\n------=_NextPart_01DB\r\nContent-Type: image/png\r\nContent-Transfer-Encoding: base64\r\n\r\niVBORw0KGgo=\r\n\r\n------=_NextPart_01DB--\r\n";
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AGENTS.md"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    private static async Task<string> ContentAsync(Fixture fixture, string relativePath)
    {
        await using var connection = await fixture.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT content FROM local_ai_documents WHERE relative_path=$path";
        command.Parameters.AddWithValue("$path", relativePath);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static string Step()
    {
        // A B-spline record larger than the 64 KB record buffer is skipped without being kept.
        var spline = "#13=B_SPLINE_CURVE_WITH_KNOTS('',3,(" + string.Join(",", Enumerable.Repeat("#100000", 20_000))
            + "),.UNSPECIFIED.,.F.,.F.,(4,4),(0.,1.),.UNSPECIFIED.);";
        return """
            ISO-10303-21;
            HEADER;
            /* exported; for tests */
            FILE_DESCRIPTION(('CATIA V5 STEP Exchange'),'2;1');
            FILE_NAME('ASSY-100.stp','none',('Dana'),('Meimad'),'ST-DEVELOPER','CATIA Version 5 Release 2022','none');
            FILE_SCHEMA(('AUTOMOTIVE_DESIGN { 1 0 10303 214 1 1 1 1 }'));
            ENDSEC;
            DATA;
            #1=PRODUCT('ASSY-100','Bracket assembly','',(#90));
            #2=PRODUCT_DEFINITION_FORMATION('B','',#1);
            #3=PRODUCT_DEFINITION('design','',#2,#91);
            #4=PRODUCT('PN-STEP-7','Pin','Pin \X2\05E905DC05D505DD\X0\ it''s',(#90));
            #5=PRODUCT_DEFINITION_FORMATION_WITH_SPECIFIED_SOURCE('A','',#4,.NOT_KNOWN.);
            #6=PRODUCT_DEFINITION('design','',#5,#91);
            #7=NEXT_ASSEMBLY_USAGE_OCCURRENCE('1','Pin.1','',#3,#6,$);
            #8=NEXT_ASSEMBLY_USAGE_OCCURRENCE('2','Pin.2','',#3,#6,$);
            #9=(LENGTH_UNIT()NAMED_UNIT(*)SI_UNIT(.MILLI.,.METRE.));
            #10=DESCRIPTIVE_REPRESENTATION_ITEM('material','Ti-6Al-4V');
            #11=MANIFOLD_SOLID_BREP('Pin',#12);

            """ + spline + """

            #14=DESCRIPTIVE_REPRESENTATION_ITEM('finish',
              'anodize; after machining');
            ENDSEC;
            END-ISO-10303-21;
            """;
    }

    private static byte[] Workbook()
    {
        const string main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        const string worksheet = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet";
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Entry(string name, string xml)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                writer.Write(xml);
            }
            Entry("xl/workbook.xml", $"""<workbook xmlns="{main}" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Tools" sheetId="1" r:id="rId1"/><sheet name="Lookup" sheetId="2" state="hidden" r:id="rId2"/></sheets></workbook>""");
            Entry("xl/_rels/workbook.xml.rels", $"""<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="{worksheet}" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="{worksheet}" Target="worksheets/sheet2.xml"/></Relationships>""");
            Entry("xl/sharedStrings.xml", $"""<sst xmlns="{main}"><si><t>Tool list 2026</t></si><si><t>Tool</t></si><si><t>Diameter</t></si><si><t>Received</t></si><si><t>EM-12</t></si><si><t>Coolant</t></si><si><t>HIDDEN-VALUE X</t></si><si><t>חומר</t></si></sst>""");
            Entry("xl/styles.xml", $"""<styleSheet xmlns="{main}"><numFmts count="1"><numFmt numFmtId="164" formatCode="dd/mm/yyyy;@"/></numFmts><cellXfs count="3"><xf numFmtId="0"/><xf numFmtId="164"/><xf numFmtId="2"/></cellXfs></styleSheet>""");
            Entry("xl/worksheets/sheet1.xml", $"""
                <worksheet xmlns="{main}"><sheetData>
                <row r="1"><c r="A1" t="s"><v>0</v></c></row>
                <row r="2"><c r="A2" t="s"><v>1</v></c><c r="B2" t="s"><v>2</v></c><c r="C2" t="s"><v>3</v></c><c r="D2" t="s"><v>5</v></c></row>
                <row r="3"><c r="A3" t="s"><v>4</v></c><c r="B3" s="2"><v>12</v></c><c r="C3" s="1"><v>46237</v></c><c r="D3"><v>0.30000000000000004</v></c><c r="E3" t="s"><v>7</v></c></row>
                </sheetData></worksheet>
                """);
            Entry("xl/worksheets/sheet2.xml", $"""<worksheet xmlns="{main}"><sheetData><row r="1"><c r="A1" t="s"><v>6</v></c></row></sheetData></worksheet>""");
        }
        return memory.ToArray();
    }
}
