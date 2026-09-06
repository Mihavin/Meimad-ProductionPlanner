using System.Text;
using Meimad.Planner.Server.Application.ProductionPackages;

namespace Meimad.Planner.Server.Tests.ProductionPackages;

public sealed class NcPackageTemplateTransformerTests
{
    private static readonly string[] LegacyTemplate =
    [
        "%", "O1234", "(MEIMAD PACKAGE VERIFY V1 NCID=483921)",
        "(MEIMAD PACKAGE CYCLE START V1)", "G90", "M30",
        "(MEIMAD PACKAGE CYCLE END V1)", "%"
    ];

    private static readonly string[] CanonicalTemplate =
    [
        "%", "O1234",
        "(PART: [[MEIMAD:PART_NAME]])",
        "(OPERATION: [[MEIMAD:OPERATION_NAME]])",
        "(RUN: [[MEIMAD:PRODUCTION_RUN_ID]])",
        "(PACKAGE: [[MEIMAD:PRODUCTION_PACKAGE_ID]])",
        "(MACHINE: [[MEIMAD:MACHINE_ID]])",
        "(NC RELEASE: [[MEIMAD:NC_RELEASE_ID]])",
        "(OFFSET LOADER: [[MEIMAD:OFFSET_LOADER_RELEASE_ID]])",
        "[[MEIMAD:VERIFICATION_HOOK]]",
        "[[MEIMAD:EVENT_CONTEXT]]",
        "(CAM NOTE: WRONG PART NAME)", "G90", "M30", "%"
    ];

    [Fact]
    public void Verification_enabled_resolves_all_markers_to_machine_configuration()
    {
        var bytes = NcPackageTemplateTransformer.Transform(
            LegacyTemplate, new(true, 9002, 10, 10504), out var ncId);
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
            LegacyTemplate, new(false, 9002, 10, 10504), out _);
        var text = Encoding.ASCII.GetString(bytes);

        Assert.DoesNotContain("MEIMAD PACKAGE ", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MEIMAD VERIFY V1", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DPRNT[MEIMAD", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("G90", text);
    }

    [Fact]
    public void Canonical_template_resolves_authoritative_values_and_keeps_unrelated_cam_text()
    {
        var bytes = NcPackageTemplateTransformer.TransformCanonical(
            CanonicalTemplate, new(true, 9002, 10, 10504),
            new("SERVER PART", "SERVER OPERATION", "run-1", "package-1", "machine-1",
                "release-1", "loader-1"), 483921, out var protocol);
        var text = Encoding.ASCII.GetString(bytes);

        Assert.Equal(2, protocol);
        Assert.Contains("PART: SERVER PART", text, StringComparison.Ordinal);
        Assert.Contains("OPERATION: SERVER OPERATION", text, StringComparison.Ordinal);
        Assert.Contains("CAM NOTE: WRONG PART NAME", text, StringComparison.Ordinal);
        Assert.Contains("G65 P9002 A483921. (MEIMAD VERIFY V1)", text, StringComparison.Ordinal);
        Assert.Contains("DPRNT[MEIMAD/V/2/CONTEXT/PACKAGE/package-1/RUN/run-1/MACHINE/machine-1/NCRELEASE/release-1/MACROVERSION/10/PROGRAM/483921]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("[[MEIMAD:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Canonical_verification_disabled_removes_hook_and_resolves_loader_as_not_applicable()
    {
        var bytes = NcPackageTemplateTransformer.TransformCanonical(
            CanonicalTemplate, new(false, 9002, 10, 10504),
            new("PART", "OP", "run-1", "package-1", "machine-1", "release-1", null),
            483921, out _);
        var text = Encoding.ASCII.GetString(bytes);

        Assert.DoesNotContain("MEIMAD VERIFY V1", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OFFSET LOADER: NOT_APPLICABLE", text, StringComparison.Ordinal);
        Assert.DoesNotContain("[[MEIMAD:", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("(OPERATION OMITTED)", "production_package_placeholder_required")]
    [InlineData("[[MEIMAD:UNKNOWN_REQUIRED]]", "production_package_placeholder_unknown")]
    [InlineData("[[MEIMAD:VERIFICATION_HOOK]]\r\n[[MEIMAD:VERIFICATION_HOOK]]", "production_package_placeholder_duplicate")]
    [InlineData("[[MEIMAD:PART_NAME]", "production_package_placeholder_malformed")]
    public void Canonical_schema_rejects_missing_unknown_duplicate_and_malformed_tokens(
        string replacement,
        string expectedCode)
    {
        var source = CanonicalTemplate.Select(line =>
            line == "(OPERATION: [[MEIMAD:OPERATION_NAME]])" ? replacement : line).ToArray();
        if (expectedCode == "production_package_placeholder_duplicate")
            source = CanonicalTemplate.Select(line => line == "[[MEIMAD:VERIFICATION_HOOK]]" ? replacement : line).ToArray();
        if (expectedCode == "production_package_placeholder_malformed")
            source = CanonicalTemplate.Select(line => line == "(PART: [[MEIMAD:PART_NAME]])" ? replacement : line).ToArray();

        var error = Assert.Throws<ProductionPackageBuildException>(() =>
            NcPackagePlaceholderSchema.ValidateCanonical(source));
        Assert.Equal(expectedCode, error.Code);
    }

    private static readonly string[] CanonicalTemplateWithCycleMarkers =
    [
        "%", "O1234",
        "(PART: [[MEIMAD:PART_NAME]])",
        "(OPERATION: [[MEIMAD:OPERATION_NAME]])",
        "(RUN: [[MEIMAD:PRODUCTION_RUN_ID]])",
        "(PACKAGE: [[MEIMAD:PRODUCTION_PACKAGE_ID]])",
        "(MACHINE: [[MEIMAD:MACHINE_ID]])",
        "(NC RELEASE: [[MEIMAD:NC_RELEASE_ID]])",
        "(OFFSET LOADER: [[MEIMAD:OFFSET_LOADER_RELEASE_ID]])",
        "[[MEIMAD:VERIFICATION_HOOK]]",
        "[[MEIMAD:EVENT_CONTEXT]]",
        "[[MEIMAD:CYCLE_START]]", "G90", "M30", "[[MEIMAD:CYCLE_END]]", "%"
    ];

    [Fact]
    public void Canonical_cycle_markers_resolve_to_wire_format_v1_cst_cen_when_verification_enabled()
    {
        var bytes = NcPackageTemplateTransformer.TransformCanonical(
            CanonicalTemplateWithCycleMarkers, new(true, 9002, 10, 10504),
            new("PART", "OP", "run-1", "package-1", "machine-1", "release-1", "loader-1"),
            483921, out _);
        var text = Encoding.ASCII.GetString(bytes);

        Assert.Contains("DPRNT[MEIMAD/V/1/EVENT/CST/ID/NC-483921-S-#3001[80]/SEQ/#30[60]/MACROVERSION/10/PROGRAM/483921]", text, StringComparison.Ordinal);
        Assert.Contains("DPRNT[MEIMAD/V/1/EVENT/CEN/ID/NC-483921-E-#3001[80]/SEQ/#30[60]/MACROVERSION/10/PROGRAM/483921]", text, StringComparison.Ordinal);
        Assert.Contains("#10504=#30", text, StringComparison.Ordinal);
        Assert.DoesNotContain("[[MEIMAD:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Canonical_cycle_markers_are_dropped_when_verification_disabled()
    {
        var bytes = NcPackageTemplateTransformer.TransformCanonical(
            CanonicalTemplateWithCycleMarkers, new(false, 9002, 10, 10504),
            new("PART", "OP", "run-1", "package-1", "machine-1", "release-1", null),
            483921, out _);
        var text = Encoding.ASCII.GetString(bytes);

        Assert.DoesNotContain("EVENT/CST", text, StringComparison.Ordinal);
        Assert.DoesNotContain("EVENT/CEN", text, StringComparison.Ordinal);
        Assert.DoesNotContain("[[MEIMAD:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Canonical_schema_allows_omitting_both_cycle_markers()
    {
        var validation = NcPackagePlaceholderSchema.ValidateCanonical(CanonicalTemplate);
        Assert.Equal(0, validation.Counts[NcPackagePlaceholderKeys.CycleStart]);
        Assert.Equal(0, validation.Counts[NcPackagePlaceholderKeys.CycleEnd]);
    }

    [Fact]
    public void Canonical_schema_rejects_cycle_start_without_matching_cycle_end()
    {
        var source = CanonicalTemplate.Append("[[MEIMAD:CYCLE_START]]").ToArray();
        var error = Assert.Throws<ProductionPackageBuildException>(() =>
            NcPackagePlaceholderSchema.ValidateCanonical(source));
        Assert.Equal("production_package_cycle_marker_unpaired", error.Code);
    }

    [Fact]
    public void Canonical_schema_rejects_cycle_end_before_cycle_start()
    {
        var source = CanonicalTemplate.Append("[[MEIMAD:CYCLE_END]]")
            .Append("[[MEIMAD:CYCLE_START]]").ToArray();
        var error = Assert.Throws<ProductionPackageBuildException>(() =>
            NcPackagePlaceholderSchema.ValidateCanonical(source));
        Assert.Equal("production_package_cycle_marker_order_invalid", error.Code);
    }

    [Fact]
    public void Canonical_schema_rejects_duplicate_cycle_start()
    {
        var source = CanonicalTemplate
            .Append("[[MEIMAD:CYCLE_START]]")
            .Append("[[MEIMAD:CYCLE_START]]")
            .Append("[[MEIMAD:CYCLE_END]]").ToArray();
        var error = Assert.Throws<ProductionPackageBuildException>(() =>
            NcPackagePlaceholderSchema.ValidateCanonical(source));
        Assert.Equal("production_package_placeholder_duplicate", error.Code);
    }
}
