using Meimad.Planner.Server.Application.MachineEvidence;

namespace Meimad.Planner.Server.Tests.MachineEvidence;

/// <summary>The §20.9 work offset parser state machine and the line normalizer, without any Machine or database.</summary>
public sealed class OffsetEvidenceParserTests
{
    internal static readonly string[] HaasFrame =
    [
        "MEIMAD/OFFSET-START/SESSION/WO-10-20261012-63000-1/MACHINE/10/PROGRAM/654321/OPERATION/20/RELEASE/654321",
        "MEIMAD/OFFSET/SESSION/WO-10-20261012-63000-1/OFFSET/G54/UNITS/21/AXES/XYZ",
        "MEIMAD/OFFSET/SESSION/WO-10-20261012-63000-1/X/125.4020/Y/-80.0160/Z/42.6910",
        "MEIMAD/OFFSET-END/SESSION/WO-10-20261012-63000-1"
    ];

    [Fact]
    public void A_complete_multi_line_frame_completes_with_identity_units_and_values()
    {
        var parser = new OffsetEvidenceParser();
        Assert.Equal(OffsetEvidenceParseOutcome.Pending, parser.Accept(HaasFrame[0]).Outcome);
        Assert.Equal("WO-10-20261012-63000-1", parser.OpenSession);
        Assert.Equal(OffsetEvidenceParseOutcome.Pending, parser.Accept(HaasFrame[1]).Outcome);
        Assert.Equal(OffsetEvidenceParseOutcome.Pending, parser.Accept(HaasFrame[2]).Outcome);
        var result = parser.Accept(HaasFrame[3]);

        Assert.Equal(OffsetEvidenceParseOutcome.Completed, result.Outcome);
        var message = result.Message!;
        Assert.Equal("10", message.Machine);
        Assert.Equal("654321", message.Program);
        Assert.Equal("20", message.Operation);
        Assert.Equal("654321", message.Release);
        Assert.Equal("G54", message.WorkOffset);
        Assert.Equal("MM", message.Units);
        Assert.Equal(125.402, message.Values["X"], 6);
        Assert.Equal(-80.016, message.Values["Y"], 6);
        Assert.Equal(42.691, message.Values["Z"], 6);
        Assert.Equal(4, message.RawLines.Count);
        Assert.Null(message.RejectCode);
        Assert.Null(parser.OpenSession);
        Assert.Equal(64, message.ContentHash().Length);
    }

    [Fact]
    public void The_definition_form_with_semicolons_and_value_records_is_the_same_message()
    {
        var parser = new OffsetEvidenceParser();
        parser.Accept("MEIMAD/OFFSET_START;session=WO-10-20261012-63000-1;machine=10;program=654321;operation=20;release=654321;offset=G54;units=mm");
        parser.Accept("MEIMAD/OFFSET_VALUE;session=WO-10-20261012-63000-1;axis=X;value=125.402");
        parser.Accept("MEIMAD/OFFSET_VALUE;session=WO-10-20261012-63000-1;axis=Y;value=-80.016");
        parser.Accept("MEIMAD/OFFSET_VALUE;session=WO-10-20261012-63000-1;axis=Z;value=42.691");
        var spec = parser.Accept("MEIMAD/OFFSET_END;session=WO-10-20261012-63000-1").Message!;

        var cnc = Feed(new OffsetEvidenceParser(), HaasFrame).Message!;
        Assert.Equal(cnc.ContentHash(), spec.ContentHash());
    }

    [Fact]
    public void The_single_line_form_completes_at_once()
    {
        var result = new OffsetEvidenceParser().Accept(
            "MEIMAD/OFFSET;machine=HAAS_VF3_01;program=O1234;operation=010;release=abc123;offset=G54;x=125.402;y=-80.016;z=42.691;a=0;b=0;c=90;units=mm");
        Assert.Equal(OffsetEvidenceParseOutcome.Completed, result.Outcome);
        Assert.True(result.Message!.SingleLine);
        Assert.Equal("HAAS_VF3_01", result.Message.Machine);
        Assert.Equal("ABC123", result.Message.Release);
        Assert.Equal(90, result.Message.Values["C"]);
        Assert.Null(result.Message.Session);
    }

    [Fact]
    public void FANUC_control_codes_blanks_and_line_endings_do_not_change_the_message()
    {
        // POPEN sends DC2, PCLOS DC4; parameter 6001#1 pads numbers with blanks; a bridge may add NUL.
        string[] fanuc =
        [
            "\u0012MEIMAD/OFFSET-START/SESSION/WO-10-20261012- 63000-1/MACHINE/10/PROGRAM/654321/OPERATION/20/RELEASE/654321",
            "MEIMAD/OFFSET/SESSION/WO-10-20261012- 63000-1/OFFSET/G54/UNITS/  21./AXES/XYZ\0",
            "MEIMAD/OFFSET/SESSION/WO-10-20261012- 63000-1/X/  125.4020/Y/ -80.0160/Z/   42.6910",
            "MEIMAD/OFFSET-END/SESSION/WO-10-20261012- 63000-1\u0014"
        ];
        var fromFanuc = Feed(new OffsetEvidenceParser(), fanuc).Message!;
        var fromHaas = Feed(new OffsetEvidenceParser(), HaasFrame).Message!;
        Assert.Equal(fromHaas.ContentHash(), fromFanuc.ContentHash());
        Assert.Equal(fromHaas.Values, fromFanuc.Values);
    }

    [Fact]
    public void Value_or_end_without_start_is_refused()
    {
        var parser = new OffsetEvidenceParser();
        Assert.Equal("value_without_start", parser.Accept("MEIMAD/OFFSET/SESSION/S1/X/1.0").Code);
        Assert.Equal("value_without_start", parser.Accept("MEIMAD/OFFSET-VALUE/SESSION/S1/AXIS/X/VALUE/1.0").Code);
        var end = parser.Accept("MEIMAD/OFFSET-END/SESSION/S1");
        Assert.Equal(OffsetEvidenceParseOutcome.RefusedLine, end.Outcome);
        Assert.Equal("end_without_start", end.Code);
    }

    [Fact]
    public void A_frame_cut_off_before_its_end_is_never_completed()
    {
        var parser = new OffsetEvidenceParser();
        foreach (var line in HaasFrame.Take(3))
            Assert.Equal(OffsetEvidenceParseOutcome.Pending, parser.Accept(line).Outcome);

        // The next report starts: the open session is dropped, not stored.
        var next = parser.Accept(HaasFrame[0].Replace("63000-1", "63100-1", StringComparison.Ordinal));
        Assert.Equal(OffsetEvidenceParseOutcome.Discarded, next.Outcome);
        Assert.Equal("interrupted_by_new_start", next.Code);
        Assert.Equal("WO-10-20261012-63000-1", next.Session);
        Assert.Null(next.Message);
    }

    [Fact]
    public void An_end_with_a_declared_axis_missing_drops_the_session()
    {
        var parser = new OffsetEvidenceParser();
        parser.Accept(HaasFrame[0]);
        parser.Accept(HaasFrame[1]);
        parser.Accept("MEIMAD/OFFSET/SESSION/WO-10-20261012-63000-1/X/1.0/Y/2.0");
        var result = parser.Accept(HaasFrame[3]);
        Assert.Equal(OffsetEvidenceParseOutcome.Discarded, result.Outcome);
        Assert.Equal("missing_axis", result.Code);
        Assert.Null(result.Message);
    }

    [Theory]
    [InlineData("MEIMAD/OFFSET/SESSION/WO-10-20261012-63000-1/X/1.0/X/1.0", "duplicate_axis")]
    [InlineData("MEIMAD/OFFSET/SESSION/WO-10-20261012-63000-1/X/1..0", "invalid_number")]
    [InlineData("MEIMAD/OFFSET/SESSION/WO-10-20261012-63000-1/X/ABC", "invalid_number")]
    [InlineData("MEIMAD/OFFSET/SESSION/WO-10-20261012-63000-1/UNITS/FURLONG", "invalid_units")]
    [InlineData("MEIMAD/OFFSET/SESSION/WO-10-20261012-63000-1/OFFSET/G55", "offset_mismatch")]
    [InlineData("MEIMAD/OFFSET/SESSION/WO-10-20261012-63000-1/MACHINE/14", "machine_conflict")]
    public void Invalid_content_inside_a_frame_rejects_the_frame_at_its_end(string extra, string code)
    {
        var parser = new OffsetEvidenceParser();
        parser.Accept(HaasFrame[0]);
        parser.Accept(HaasFrame[1]);
        parser.Accept(HaasFrame[2].Replace("/X/125.4020", string.Empty, StringComparison.Ordinal));
        parser.Accept(extra);
        var result = parser.Accept(HaasFrame[3]);
        Assert.Equal(OffsetEvidenceParseOutcome.Rejected, result.Outcome);
        Assert.Equal(code, result.Code);
        Assert.Equal(code, result.Message!.RejectCode);
    }

    [Fact]
    public void A_second_value_for_an_axis_from_another_line_is_a_duplicate_axis()
    {
        var parser = new OffsetEvidenceParser();
        foreach (var line in HaasFrame.Take(3)) parser.Accept(line);
        parser.Accept("MEIMAD/OFFSET-VALUE/SESSION/WO-10-20261012-63000-1/AXIS/Z/VALUE/42.7");
        var result = parser.Accept(HaasFrame[3]);
        Assert.Equal(OffsetEvidenceParseOutcome.Rejected, result.Outcome);
        Assert.Equal("duplicate_axis", result.Code);
    }

    [Fact]
    public void Missing_identity_or_units_rejects_a_complete_frame()
    {
        var withoutMachine = Feed(new OffsetEvidenceParser(),
        [
            "MEIMAD/OFFSET-START/SESSION/S1/PROGRAM/654321/OPERATION/20/RELEASE/654321",
            "MEIMAD/OFFSET/SESSION/S1/OFFSET/G54/UNITS/MM/X/1/Y/2/Z/3",
            "MEIMAD/OFFSET-END/SESSION/S1"
        ]);
        Assert.Equal("machine_missing", withoutMachine.Code);
        var withoutUnits = Feed(new OffsetEvidenceParser(),
        [
            "MEIMAD/OFFSET-START/SESSION/S1/MACHINE/10/PROGRAM/654321/OPERATION/20/RELEASE/654321",
            "MEIMAD/OFFSET/SESSION/S1/OFFSET/G54/X/1/Y/2/Z/3",
            "MEIMAD/OFFSET-END/SESSION/S1"
        ]);
        Assert.Equal("units_missing", withoutUnits.Code);
    }

    [Fact]
    public void Lines_of_another_session_do_not_join_the_open_frame()
    {
        var parser = new OffsetEvidenceParser();
        parser.Accept(HaasFrame[0]);
        Assert.Equal("session_mismatch", parser.Accept("MEIMAD/OFFSET/SESSION/OTHER/X/1.0").Code);
        Assert.Equal("session_mismatch", parser.Accept("MEIMAD/OFFSET-END/SESSION/OTHER").Code);
        Assert.Equal("WO-10-20261012-63000-1", parser.OpenSession);
        foreach (var line in HaasFrame.Skip(1).Take(2)) parser.Accept(line);
        Assert.Equal(OffsetEvidenceParseOutcome.Completed, parser.Accept(HaasFrame[3]).Outcome);
    }

    [Fact]
    public void Other_DPRNT_lines_are_ignored_and_do_not_disturb_an_open_frame()
    {
        var parser = new OffsetEvidenceParser();
        parser.Accept(HaasFrame[0]);
        foreach (var line in new[] { "30P647004101-001", "MEIMAD/V/1/EVENT/CST/ID/X/SEQ/1", "MEIMAD/V/2/CONTEXT/PACKAGE/1", "", "T12 D=10.000" })
            Assert.Equal(OffsetEvidenceParseOutcome.Ignored, parser.Accept(line).Outcome);
        foreach (var line in HaasFrame.Skip(1).Take(2)) parser.Accept(line);
        Assert.Equal(OffsetEvidenceParseOutcome.Completed, parser.Accept(HaasFrame[3]).Outcome);
    }

    [Theory]
    [InlineData("MEIMAD/OFFSETX/SESSION/S1", "unsupported_record")]
    [InlineData("MEIMAD/OFFSET-START/SESSION", "invalid_field_pairs")]
    [InlineData("MEIMAD/OFFSET-START/COLOUR/RED", "unsupported_field")]
    [InlineData("MEIMAD/OFFSET-START/SESSION/S1/SESSION/S2", "duplicate_field")]
    [InlineData("MEIMAD/OFFSET-START/SESSION/S#1", "invalid_field_value")]
    public void Malformed_work_offset_lines_are_recognized_and_refused(string line, string code)
    {
        Assert.True(DprntLineNormalizer.IsWorkOffsetLine(line));
        var normalized = DprntLineNormalizer.Normalize(line);
        Assert.Null(normalized.Record);
        Assert.Equal(code, normalized.ErrorCode);
        Assert.Equal(code, new OffsetEvidenceParser().Accept(line).Code);
    }

    [Theory]
    [InlineData("G54", "G54")]
    [InlineData("g 59", "G59")]
    [InlineData("G54.1 P1", "G54.1 P1")]
    [InlineData("G54.1P48", "G54.1 P48")]
    [InlineData("G541P7", "G54.1 P7")]
    [InlineData("G54-1P300", "G54.1 P300")]
    [InlineData("G154 P99", "G154 P99")]
    [InlineData("G110", "G110")]
    [InlineData("G15 H1", "G15 H1")]
    [InlineData("H12", "G15 H12")]
    public void Work_offset_names_have_one_stored_spelling(string value, string canonical) =>
        Assert.Equal(canonical, WorkOffsetNames.Parse(value)!.Canonical);

    [Theory]
    [InlineData("G53")]
    [InlineData("G60")]
    [InlineData("G54.1")]
    [InlineData("G54.1 P0")]
    [InlineData("WCS1")]
    public void Other_names_are_no_work_offsets(string value) => Assert.Null(WorkOffsetNames.Parse(value));

    [Theory]
    [InlineData("MM", "MM")]
    [InlineData("21", "MM")]
    [InlineData("21.", "MM")]
    [InlineData("21.000", "MM")]
    [InlineData("INCH", "INCH")]
    [InlineData("20.", "INCH")]
    [InlineData("22", null)]
    [InlineData("CM", null)]
    public void Units_come_as_words_or_as_the_modal_G_code_number(string value, string? expected) =>
        Assert.Equal(expected, OffsetEvidenceParser.NormalizeUnits(value));

    [Theory]
    [InlineData("125.4020", 125.402)]
    [InlineData("-80.016", -80.016)]
    [InlineData("+3", 3)]
    [InlineData("0125.", 125)]
    [InlineData(".5", 0.5)]
    public void Numbers_as_controls_print_them_are_read(string text, double expected)
    {
        Assert.True(OffsetEvidenceParser.TryParseNumber(text, out var value));
        Assert.Equal(expected, value, 9);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-")]
    [InlineData("1.2.3")]
    [InlineData("1E5")]
    [InlineData("--1")]
    [InlineData("NAN")]
    public void Anything_else_is_no_number(string text) => Assert.False(OffsetEvidenceParser.TryParseNumber(text, out _));

    [Fact]
    public void Sequence_numbers_are_kept_as_evidence_only()
    {
        var parser = new OffsetEvidenceParser();
        parser.Accept(HaasFrame[0] + "/SEQ/17");
        foreach (var line in HaasFrame.Skip(1).Take(2)) parser.Accept(line);
        var message = parser.Accept(HaasFrame[3]).Message!;
        Assert.Equal(17, message.Sequence);
        // The hash ignores the sequence: a controller that resets its counter cannot change the evidence identity.
        Assert.Equal(Feed(new OffsetEvidenceParser(), HaasFrame).Message!.ContentHash(), message.ContentHash());
    }

    private static OffsetEvidenceParseResult Feed(OffsetEvidenceParser parser, IEnumerable<string> lines)
    {
        OffsetEvidenceParseResult result = OffsetEvidenceParseResult.Ignored;
        foreach (var line in lines) result = parser.Accept(line);
        return result;
    }
}
