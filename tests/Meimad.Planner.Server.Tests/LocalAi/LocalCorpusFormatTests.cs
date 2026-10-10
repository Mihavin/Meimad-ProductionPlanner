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
