using Meimad.Planner.Server.Application.LegacyImport;
using Meimad.Planner.Server.Domain.LegacyImport;

namespace Meimad.Planner.Server.Tests.LegacyImport;

public sealed class OpenXmlLegacyWorkbookReaderTests
{
    [Fact]
    public async Task Reads_hebrew_sheets_cached_formula_and_1900_date_source()
    {
        var bytes = LegacyWorkbookFixture.Create();
        await using var stream = new MemoryStream(bytes);

        var workbook = await new OpenXmlLegacyWorkbookReader().ReadAsync(stream, "fixture.xlsx", default);

        Assert.Equal([LegacyWorkbookFixture.PlanningSheet, LegacyWorkbookFixture.OpenOrdersSheet], workbook.Sheets.Select(sheet => sheet.Name));
        var formula = workbook.Sheets[0].Cell(3, 3)!;
        Assert.Equal("3013", formula.Value);
        Assert.Equal("formula_cached", formula.Kind);
        Assert.Equal("[1]Lookup!A1", formula.Formula);
        Assert.Equal("46237", workbook.Sheets[0].Cell(3, 8)!.Value);
    }

    [Fact]
    public async Task Rejects_1904_date_system_and_duplicate_normalized_parts()
    {
        await using var dateStream = new MemoryStream(LegacyWorkbookFixture.Create(date1904: true));
        var dateError = await Assert.ThrowsAsync<LegacyWorkbookFormatException>(() =>
            new OpenXmlLegacyWorkbookReader().ReadAsync(dateStream, "date1904.xlsx", default));
        Assert.Equal("unsupported_date_system", dateError.Code);

        await using var duplicateStream = new MemoryStream(LegacyWorkbookFixture.Create(duplicateWorkbookPart: true));
        var duplicateError = await Assert.ThrowsAsync<LegacyWorkbookFormatException>(() =>
            new OpenXmlLegacyWorkbookReader().ReadAsync(duplicateStream, "duplicate.xlsx", default));
        Assert.Equal("duplicate_openxml_part", duplicateError.Code);
    }
}
