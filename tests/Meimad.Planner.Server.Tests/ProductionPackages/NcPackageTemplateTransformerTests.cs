using System.Text;
using Meimad.Planner.Server.Application.ProductionPackages;

namespace Meimad.Planner.Server.Tests.ProductionPackages;

public sealed class NcPackageTemplateTransformerTests
{
    private static readonly string[] Template =
    [
        "%", "O1234", "(MEIMAD PACKAGE VERIFY V1 NCID=483921)",
        "(MEIMAD PACKAGE CYCLE START V1)", "G90", "M30",
        "(MEIMAD PACKAGE CYCLE END V1)", "%"
    ];

    [Fact]
    public void Verification_enabled_resolves_all_markers_to_machine_configuration()
    {
        var bytes = NcPackageTemplateTransformer.Transform(
            Template, new(true, 9002, 10, 10504), out var ncId);
        var text = Encoding.ASCII.GetString(bytes);

        Assert.Equal(483921, ncId);
        Assert.Contains("G65 P9002 A483921. (MEIMAD VERIFY V1)", text);
        Assert.Contains("EVENT/CST", text);
        Assert.Contains("EVENT/CEN", text);
        Assert.Contains("#10504=#30", text);
        Assert.Contains("MACROVERSION/10/PROGRAM/483921", text);
        Assert.DoesNotContain("MEIMAD PACKAGE ", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Verification_disabled_removes_all_markers_and_active_verification_content()
    {
        var bytes = NcPackageTemplateTransformer.Transform(
            Template, new(false, 9002, 10, 10504), out _);
        var text = Encoding.ASCII.GetString(bytes);

        Assert.DoesNotContain("MEIMAD PACKAGE ", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MEIMAD VERIFY V1", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DPRNT[MEIMAD", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("G90", text);
    }
}
