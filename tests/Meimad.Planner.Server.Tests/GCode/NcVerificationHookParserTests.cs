using Meimad.Planner.Server.Application.GCode;

namespace Meimad.Planner.Server.Tests.GCode;

public sealed class NcVerificationHookParserTests
{
    [Fact]
    public void Parses_G65_hook_as_first_executable_block()
    {
        var value = NcVerificationHookParser.ParseRequired([
            "%", "O1234 (PART)", "(APPROVED NC)",
            "N10 G65 P9002 A483921. (MEIMAD VERIFY V1)", "G90", "M30", "%"]);

        Assert.Equal(1, value.HookVersion);
        Assert.Equal("G65", value.InvocationKind);
        Assert.Equal(9002, value.InvocationNumber);
        Assert.Equal(483921, value.NcIdentityToken);
        Assert.Equal(4, value.LineNumber);
    }

    [Fact]
    public void Parses_configurable_custom_G_code_hook()
    {
        var value = NcVerificationHookParser.ParseRequired([
            "O4321", "G605 A583921 (MEIMAD VERIFY V1)", "M30"]);

        Assert.Equal("CUSTOM_GCODE", value.InvocationKind);
        Assert.Equal(605, value.InvocationNumber);
        Assert.Equal(583921, value.NcIdentityToken);
    }

    [Theory]
    [InlineData("O1234\nM30", "verification_hook_required")]
    [InlineData("O1234\nG90\nG65 P9002 A483921 (MEIMAD VERIFY V1)\nM30", "verification_hook_not_first")]
    [InlineData("O1234\nG65 P9002 A483921 (MEIMAD VERIFY V1)\nG65 P9002 A583921 (MEIMAD VERIFY V1)\nM30", "verification_hook_ambiguous")]
    [InlineData("O1234\nG65 P8002 A483921 (MEIMAD VERIFY V1)\nM30", "verification_hook_invalid")]
    [InlineData("O1234\nG000 A483921 (MEIMAD VERIFY V1)\nM30", "verification_hook_invalid")]
    [InlineData("O1234\nG65 P9002 A000001 (MEIMAD VERIFY V1)\nM30", "verification_hook_invalid")]
    public void Missing_late_duplicate_or_malformed_hook_fails_closed(string program, string code)
    {
        var exception = Assert.Throws<GCodeValidationException>(() =>
            NcVerificationHookParser.ParseRequired(program.Split('\n')));

        Assert.Equal(code, exception.Code);
        Assert.Equal("gCodeFile", exception.Field);
    }
}
