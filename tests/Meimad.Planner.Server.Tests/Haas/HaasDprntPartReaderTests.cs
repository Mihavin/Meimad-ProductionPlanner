using Meimad.Planner.Server.Infrastructure.Haas;

namespace Meimad.Planner.Server.Tests.Haas;

public sealed class HaasDprntPartReaderTests
{
    [Theory]
    [InlineData("30P647004101-001", "30P647004101-001")]
    [InlineData(" 16e2509-7psofi-1 ", "16E2509-7PSOFI-1")]
    public void Parses_control_authored_part_number_lines(string line, string expected)
    {
        Assert.True(HaasDprntPartReader.TryParsePartName(line, out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("pingret")]
    [InlineData("1500.CNC")]
    [InlineData("O1500")]
    [InlineData("Part Name: 30P647004101-001")]
    public void Rejects_non_part_or_unstructured_protocol_lines(string line)
    {
        Assert.False(HaasDprntPartReader.TryParsePartName(line, out var actual));
        Assert.Null(actual);
    }
}
