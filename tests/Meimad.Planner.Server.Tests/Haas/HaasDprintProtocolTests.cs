using Meimad.Planner.Server.Infrastructure.Haas;

namespace Meimad.Planner.Server.Tests.Haas;

public sealed class HaasDprintProtocolTests
{
    [Fact]
    public void Offset_loader_event_parses_only_with_complete_ordered_evidence()
    {
        const string line = "MEIMAD/V/1/EVENT/OLC/ID/VF3-1817/SEQ/1817/MACROVERSION/3/RUN/RUN-42/PROGRAM/O1234/OFFSETRELEASE/483920/NONCE/731841";

        Assert.True(HaasDprintProtocol.TryParse(line, out var value, out var error), error);
        Assert.Equal("OFFSET_LOADER_COMPLETED", value!.EventType);
        Assert.Equal("VF3-1817", value.SourceEventId);
        Assert.Equal(1817, value.Sequence);
        Assert.Equal(3, value.MacroVersion);
        Assert.Equal("RUN-42", value.ProductionRunId);
        Assert.Equal("O1234", value.ProgramIdentity);
        Assert.Equal(483920, value.OffsetReleaseToken);
        Assert.Equal(731841, value.Nonce);
    }

    [Theory]
    [InlineData("CST", "CYCLE_START")]
    [InlineData("CEN", "CYCLE_END")]
    public void Production_cycle_event_requires_source_identity_and_sequence(
        string wireCode, string eventType)
    {
        var line = $"MEIMAD/V/1/EVENT/{wireCode}/ID/VF3-201/SEQ/201/MACROVERSION/3/RUN/RUN-42/PROGRAM/654321";

        Assert.True(HaasDprintProtocol.TryParse(line, out var value, out var error), error);
        Assert.Equal(eventType, value!.EventType);
        Assert.Equal("VF3-201", value.SourceEventId);
        Assert.Equal(201, value.Sequence);
        Assert.Equal("RUN-42", value.ProductionRunId);
        Assert.Equal("654321", value.ProgramIdentity);
    }

    [Theory]
    [InlineData("EVENT/OLC/OFFSETRELEASE/1/NONCE/2", "invalid_prefix")]
    [InlineData("MEIMAD/V/2/EVENT/CST/ID/E1/SEQ/1/MACROVERSION/3", "unsupported_protocol_version")]
    [InlineData("MEIMAD/V/1/EVENT/OLC/ID/E1/SEQ/1/MACROVERSION/3/OFFSETRELEASE/1", "missing_offset_evidence")]
    [InlineData("MEIMAD/V/1/EVENT/OLC/ID/E1/SEQ/1/MACROVERSION/3/OFFSETRELEASE/100000/NONCE/99999", "invalid_offset_evidence_range")]
    [InlineData("MEIMAD/V/1/EVENT/CST/ID/E1/SEQ/1/MACROVERSION/3/NONCE/1", "unexpected_offset_evidence")]
    [InlineData("MEIMAD/EVENT/CST/V/1/ID/E1/SEQ/1/MACROVERSION/3", "invalid_field_order")]
    [InlineData("MEIMAD/V/1/EVENT/CST/ID/E1/SEQ/1/MACROVERSION/3/SECRET/X", "invalid_optional_field")]
    [InlineData("MEIMAD|V=1|EVENT=OLC|ID=E1|SEQ=1|MACROVERSION=3", "invalid_encoding_or_length")]
    public void Invalid_or_ambiguous_lines_fail_closed(string line, string expectedError)
    {
        Assert.False(HaasDprintProtocol.TryParse(line, out var value, out var error));
        Assert.Null(value);
        Assert.Equal(expectedError, error);
    }
}
