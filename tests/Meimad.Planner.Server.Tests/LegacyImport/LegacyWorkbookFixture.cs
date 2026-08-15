using System.IO.Compression;
using System.Text;

namespace Meimad.Planner.Server.Tests.LegacyImport;

internal static class LegacyWorkbookFixture
{
    internal const string PlanningSheet = "תכנית ייצור";
    internal const string OpenOrdersSheet = "גיליון1";

    internal static byte[] Create(bool date1904 = false, bool duplicateWorkbookPart = false, string marker = "")
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(archive, "xl/workbook.xml", $$"""
                <?xml version="1.0" encoding="utf-8"?>
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"
                          xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <workbookPr date1904="{{(date1904 ? "1" : "0")}}"/>
                  <sheets>
                    <sheet name="{{PlanningSheet}}" sheetId="1" r:id="rId1"/>
                    <sheet name="{{OpenOrdersSheet}}" sheetId="2" r:id="rId2"/>
                  </sheets>
                </workbook>
                """);
            if (duplicateWorkbookPart)
            {
                Add(archive, "XL/WORKBOOK.XML", "<duplicate/>");
            }
            Add(archive, "xl/_rels/workbook.xml.rels", """
                <?xml version="1.0" encoding="utf-8"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
                  <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet2.xml"/>
                </Relationships>
                """);
            Add(archive, "xl/worksheets/sheet1.xml", $$"""
                <?xml version="1.0" encoding="utf-8"?>
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                  <sheetData>
                    <row r="1"><c r="F1" t="inlineStr"><is><t>מכונה 1 - 3 צירים</t></is></c></row>
                    <row r="2"><c r="B2" t="inlineStr"><is><t>מקט</t></is></c><c r="F2" t="inlineStr"><is><t>כמות</t></is></c></row>
                    <row r="3"><c r="A3" t="inlineStr"><is><t>Customer</t></is></c><c r="B3" t="inlineStr"><is><t>PN-1</t></is></c><c r="C3"><f>[1]Lookup!A1</f><v>3013</v></c><c r="F3"><v>2</v></c><c r="H3"><v>46237</v></c></row>
                    <row r="4"><c r="A4" t="inlineStr"><is><t>Customer</t></is></c><c r="B4" t="inlineStr"><is><t>PN-2</t></is></c><c r="F4"><v>3</v></c><c r="J4" t="e"><v>#REF!</v></c></row>
                    <row r="5"><c r="A5" t="inlineStr"><is><t>Customer</t></is></c><c r="B5" t="inlineStr"><is><t>PN-3</t></is></c><c r="F5"><v>4</v></c><c r="L5" t="inlineStr"><is><t>{{marker}}</t></is></c></row>
                  </sheetData>
                </worksheet>
                """);
            Add(archive, "xl/worksheets/sheet2.xml", """
                <?xml version="1.0" encoding="utf-8"?>
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                  <sheetData>
                    <row r="1">
                      <c r="A1" t="inlineStr"><is><t>מספר פריט</t></is></c>
                      <c r="B1" t="inlineStr"><is><t>מספר הזמנה</t></is></c>
                      <c r="C1" t="inlineStr"><is><t>מספר שורה</t></is></c>
                      <c r="D1" t="inlineStr"><is><t>שם לקוח</t></is></c>
                      <c r="E1" t="inlineStr"><is><t>תאריך אספקה</t></is></c>
                      <c r="H1" t="inlineStr"><is><t>יתרה לאספקה</t></is></c>
                    </row>
                    <row r="2"><c r="A2" t="inlineStr"><is><t>PN-NEW</t></is></c><c r="B2" t="inlineStr"><is><t>O-NEW</t></is></c><c r="C2"><v>1</v></c><c r="D2" t="inlineStr"><is><t>New Customer</t></is></c><c r="E2"><v>46237</v></c><c r="H2"><v>5</v></c><c r="O2" t="inlineStr"><is><t>New Part</t></is></c></row>
                  </sheetData>
                </worksheet>
                """);
        }
        return stream.ToArray();
    }

    private static void Add(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
