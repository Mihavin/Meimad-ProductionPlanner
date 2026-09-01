using Meimad.Planner.Server.Application.GCode;

namespace Meimad.Planner.Server.Tests.GCode;

public sealed class NcVerificationHookParserTests
{
    [Fact]
    public void Parses_package_placeholder_before_first_executable_block()
    {
        var value = NcVerificationHookParser.ParseRequired([
            "%", "O1234 (PART)", "(APPROVED NC)",
            "(MEIMAD PACKAGE VERIFY V1 NCID=483921)", "G90", "M30", "%"]);

        Assert.Equal(1, value.HookVersion);
        Assert.Equal("G65", value.InvocationKind);
        Assert.Equal(9002, value.InvocationNumber);
        Assert.Equal(483921, value.NcIdentityToken);
        Assert.Equal(4, value.LineNumber);
    }

    [Fact]
    public void Accepts_one_optional_cycle_placeholder_pair()
    {
        var value = NcVerificationHookParser.ParseRequired([
            "O4321", "(MEIMAD PACKAGE VERIFY V1 NCID=583921)",
            "(MEIMAD PACKAGE CYCLE START V1)", "G90",
            "(MEIMAD PACKAGE CYCLE END V1)", "M30"]);

        Assert.Equal("G65", value.InvocationKind);
        Assert.Equal(9002, value.InvocationNumber);
        Assert.Equal(583921, value.NcIdentityToken);
    }

    [Theory]
    [InlineData("O1234\nM30", "verification_placeholder_required")]
    [InlineData("O1234\nG90\n(MEIMAD PACKAGE VERIFY V1 NCID=483921)\nM30", "verification_placeholder_not_first")]
    [InlineData("O1234\n(MEIMAD PACKAGE VERIFY V1 NCID=483921)\n(MEIMAD PACKAGE VERIFY V1 NCID=583921)\nM30", "verification_placeholder_ambiguous")]
    [InlineData("O1234\nG65 P9002 A483921 (MEIMAD VERIFY V1)\nM30", "verification_executable_not_allowed_in_template")]
    [InlineData("O1234\n(MEIMAD PACKAGE VERIFY V1 NCID=483921)\n(MEIMAD PACKAGE CYCLE START V1)\nM30", "verification_cycle_placeholders_invalid")]
    [InlineData("O1234\n(MEIMAD PACKAGE VERIFY V1 NCID=483921)\n(MEIMAD PACKAGE CYCLE END V1)\nG90\n(MEIMAD PACKAGE CYCLE START V1)\nM30", "verification_cycle_placeholders_invalid")]
    public void Missing_late_duplicate_or_malformed_placeholder_fails_closed(string program, string code)
    {
        var exception = Assert.Throws<GCodeValidationException>(() =>
            NcVerificationHookParser.ParseRequired(program.Split('\n')));

        Assert.Equal(code, exception.Code);
        Assert.Equal("gCodeFile", exception.Field);
    }
}
