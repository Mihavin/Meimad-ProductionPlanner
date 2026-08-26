using Meimad.Planner.Server.Application.Haas;
using Meimad.Planner.Server.Infrastructure.Haas;

namespace Meimad.Planner.Server.Tests.Haas;

public sealed class NcHeaderParserTests
{
    private readonly NcHeaderParser parser = new();

    [Theory]
    [InlineData("(PART: 456-123-A)", "456-123-A")]
    [InlineData("O1234\n(PART: 456-123-A)", "456-123-A")]
    [InlineData("%\r\nO1234\r\n( part =  456-123-A  )", "456-123-A")]
    [InlineData("(COMMENT)\n(PART NAME: 456-123-A)\n(REV: B)", "456-123-A")]
    [InlineData("(Pärt note)\n(PART: חלק-123)", "חלק-123")]
    public void Shared_parser_extracts_part_identity_from_header_variants(string text, string expected)
    {
        var value = parser.Parse(text.Split(["\r\n", "\n"], StringSplitOptions.None));

        Assert.True(value.IsValid);
        Assert.Equal(expected, value.PartName);
    }

    [Fact]
    public void Shared_parser_extracts_part_identity_from_meimad_cam_program_line()
    {
        var value = parser.Parse(["%", "O1000 (16E2509-7PSOFI-1_NC1)"]);

        Assert.True(value.IsValid);
        Assert.Equal("16E2509-7PSOFI-1", value.PartName);
        Assert.Equal("O1000", value.ProgramNumber);
    }

    [Fact]
    public void Parser_extracts_structured_fields_and_program_number()
    {
        var value = parser.Parse(["%", "O1234", "(PART: PART-X)",
            "(CASE: CASE-7)", "(OPERATION: 20)", "(REVISION: C)"]);

        Assert.Equal("PART-X", value.PartName);
        Assert.Equal("CASE-7", value.CaseNumber);
        Assert.Equal("20", value.Operation);
        Assert.Equal("C", value.Revision);
        Assert.Equal("O1234", value.ProgramNumber);
    }

    [Theory]
    [InlineData("")]
    [InlineData("O1234\n(NO PART HERE)")]
    [InlineData("(PART: )")]
    [InlineData("(PART: PART-A)\n(PART: PART-B)")]
    public void Missing_malformed_or_conflicting_part_is_header_invalid(string text)
    {
        var value = parser.Parse(text.Split('\n'));

        Assert.False(value.IsValid);
        Assert.Equal("HEADER_INVALID", value.Status);
        Assert.Null(value.PartName);
    }

    [Fact]
    public void Duplicate_identical_part_comments_are_not_ambiguous()
    {
        var value = parser.Parse(["(PART: PART-A)", "(part: PART-A)"]);
        Assert.Equal("PART-A", value.PartName);
    }

    [Fact]
    public void Q500_parser_returns_typed_status()
    {
        var at = DateTimeOffset.Parse("2026-08-22T08:00:00Z");
        var value = HaasMdcProtocol.ParseQ500(">PROGRAM, O01234, RUNNING, PARTS, 380\r\n", at);

        Assert.Equal("O01234", value.ProgramNumber);
        Assert.Equal("RUNNING", value.MachineStatus);
        Assert.Equal(380, value.Parts);
        Assert.Equal(at, value.Timestamp);
        Assert.Contains("O01234", value.RawResponse, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(">MACRO, 0.0\r\n", 0)]
    [InlineData(">MACRO, 1.000\r\n", 1)]
    [InlineData(">MACRO, 7294\r\n", 7294)]
    public void Q600_parser_accepts_integer_values(string raw, int expected) =>
        Assert.Equal(expected, HaasMdcProtocol.ParseMacro(raw));

    [Theory]
    [InlineData(">MACRO, 2.5\r\n")]
    [InlineData(">MACRO, NOT-A-NUMBER\r\n")]
    public void Q600_parser_rejects_non_integer_values(string raw) =>
        Assert.Throws<FormatException>(() => HaasMdcProtocol.ParseMacro(raw));
}
