using Meimad.Planner.Server.Application.GCode;

namespace Meimad.Planner.Server.Tests.GCode;

public sealed class NcTemplateFormatterTests
{
    private const string HaasProgram = """
        %
        O1234 (BRACKET)
        (T1 D10 END MILL)
        G90 G17 G40 G49 G80
        T1 M06
        G54 G0 X0 Y0
        G43 H1 Z50.
        G1 Z-5. F200.
        G0 Z50.
        G91 G28 Z0.
        M30
        %

        """;

    private static readonly NcTemplateFormatter Formatter = new();

    [Fact]
    public void Haas_program_gets_the_canonical_block_around_its_unchanged_cutting_code()
    {
        var result = Formatter.Apply(HaasProgram, "HAAS_NGC");

        Assert.True(result.Validation.IsValid, result.Validation.Message);
        Assert.True(result.Changed);
        Assert.Equal("""
            %
            O1234 (BRACKET)
            (PART: [[MEIMAD:PART_NAME]])
            (OPERATION: [[MEIMAD:OPERATION_NAME]])
            (RUN: [[MEIMAD:PRODUCTION_RUN_ID]])
            (PACKAGE: [[MEIMAD:PRODUCTION_PACKAGE_ID]])
            (MACHINE: [[MEIMAD:MACHINE_ID]])
            (NC RELEASE: [[MEIMAD:NC_RELEASE_ID]])
            (OFFSET LOADER: [[MEIMAD:OFFSET_LOADER_RELEASE_ID]])
            (T1 D10 END MILL)
            [[MEIMAD:VERIFICATION_HOOK]]
            [[MEIMAD:EVENT_CONTEXT]]
            DPRNT[[[MEIMAD:PART_NAME]]]
            [[MEIMAD:CYCLE_START]]
            G90 G17 G40 G49 G80
            T1 M06
            G54 G0 X0 Y0
            G43 H1 Z50.
            G1 Z-5. F200.
            G0 Z50.
            G91 G28 Z0.
            [[MEIMAD:CYCLE_END]]
            M30
            %

            """.ReplaceLineEndings("\n"), result.Text);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Applying_the_format_twice_changes_nothing()
    {
        foreach (var dialect in NcDialects.All)
        {
            var first = Formatter.Apply(HaasProgram, dialect);
            var second = Formatter.Apply(first.Text, dialect);

            Assert.True(first.Validation.IsValid, $"{dialect}: {first.Validation.Message}");
            Assert.False(second.Changed, dialect);
            Assert.Empty(second.Changes);
            Assert.Equal(first.Text, second.Text);
        }
    }

    [Fact]
    public void Fanuc_opens_the_print_channel_before_every_dprnt_and_closes_it_after_the_cycle_end()
    {
        var result = Formatter.Apply(HaasProgram, "FANUC_MACRO_B");
        var lines = result.Text.Split('\n');

        Assert.True(result.Validation.IsValid, result.Validation.Message);
        var hook = Array.IndexOf(lines, "[[MEIMAD:VERIFICATION_HOOK]]");
        var popen = Array.IndexOf(lines, "POPEN");
        var context = Array.IndexOf(lines, "[[MEIMAD:EVENT_CONTEXT]]");
        var print = Array.IndexOf(lines, "DPRNT[[[MEIMAD:PART_NAME]]]");
        var start = Array.IndexOf(lines, "[[MEIMAD:CYCLE_START]]");
        var end = Array.IndexOf(lines, "[[MEIMAD:CYCLE_END]]");
        var pclos = Array.IndexOf(lines, "PCLOS");
        var m30 = Array.IndexOf(lines, "M30");
        Assert.True(hook < popen && popen < context && context < print && print < start);
        Assert.True(start < end && end < pclos && pclos < m30);
        Assert.Equal(1, lines.Count(line => line == "POPEN"));
    }

    [Fact]
    public void Fanuc_program_that_already_opens_the_channel_keeps_its_popen_and_pclos()
    {
        var source = "%\nO2000\nPOPEN\nG90 G54 G0 X0 Y0\nG1 X10. F100.\nPCLOS\nM30\n%\n";

        var result = Formatter.Apply(source, "FANUC_MACRO_B");
        var lines = result.Text.Split('\n');

        Assert.True(result.Validation.IsValid, result.Validation.Message);
        Assert.Equal(1, lines.Count(line => line == "POPEN"));
        Assert.Equal(1, lines.Count(line => line == "PCLOS"));
        var popen = Array.IndexOf(lines, "POPEN");
        Assert.True(Array.IndexOf(lines, "[[MEIMAD:VERIFICATION_HOOK]]") < popen);
        Assert.True(popen < Array.IndexOf(lines, "[[MEIMAD:EVENT_CONTEXT]]"));
        Assert.True(popen < Array.IndexOf(lines, "[[MEIMAD:CYCLE_START]]"));
        Assert.True(Array.IndexOf(lines, "[[MEIMAD:CYCLE_END]]") < Array.IndexOf(lines, "PCLOS"));
    }

    [Fact]
    public void Okuma_uses_put_and_write_and_needs_no_program_header()
    {
        var source = "G15 H1\nG50 S3000\nG96 S200 M03\nT010101\nG00 X300. Z200.\nM02\n";

        var result = Formatter.Apply(source, "OKUMA_OSP");
        var lines = result.Text.Split('\n');

        Assert.True(result.Validation.IsValid, result.Validation.Message);
        Assert.Equal("(PART: [[MEIMAD:PART_NAME]])", lines[0]);
        Assert.Contains("PUT '[[MEIMAD:PART_NAME]]'", lines);
        Assert.Equal("WRITE C", lines[Array.IndexOf(lines, "PUT '[[MEIMAD:PART_NAME]]'") + 1]);
        Assert.DoesNotContain(lines, line => line.StartsWith("DPRNT", StringComparison.Ordinal));
        Assert.Equal("[[MEIMAD:CYCLE_END]]", lines[Array.IndexOf(lines, "M02") - 1]);
    }

    [Fact]
    public void Cycle_end_goes_before_the_main_program_end_not_into_a_trailing_subprogram()
    {
        var source = "O1000\nG90 G54 G0 X0 Y0 Z50.\nM98 P2000\nG91G28Z0.M30\nO2000\nG1 X10. F100.\nM99\n";

        var result = Formatter.Apply(source, "HAAS_NGC");
        var lines = result.Text.Split('\n');

        Assert.True(result.Validation.IsValid, result.Validation.Message);
        Assert.Equal("[[MEIMAD:CYCLE_END]]", lines[Array.IndexOf(lines, "G91G28Z0.M30") - 1]);
        Assert.Equal("M99", lines[^2]);
        Assert.Contains(result.Changes, change => change.Contains("before M30", StringComparison.Ordinal));
    }

    [Fact]
    public void Main_program_ending_in_m99_is_formatted_with_a_placement_warning()
    {
        var result = Formatter.Apply("O1000\nG90 G54 G0 X0 Y0 Z50.\nG1 X10. F100.\nM99\n", "HAAS_NGC");

        Assert.True(result.Validation.IsValid, result.Validation.Message);
        Assert.Contains(result.Warnings, warning => warning.Contains("M99", StringComparison.Ordinal));
    }

    [Fact]
    public void Legacy_v1_markers_become_canonical_placeholders()
    {
        var source = """
            O1500
            (MEIMAD PACKAGE VERIFY V1 NCID=123456)
            (MEIMAD PACKAGE CYCLE START V1)
            G90 G54 G0 X0 Y0 Z50.
            (MEIMAD PACKAGE CYCLE END V1)
            M30

            """.ReplaceLineEndings("\n");

        var result = Formatter.Apply(source, "HAAS_NGC");

        Assert.True(result.Validation.IsValid, result.Validation.Message);
        Assert.DoesNotContain("MEIMAD PACKAGE", result.Text, StringComparison.Ordinal);
        Assert.Equal(1, Count(result.Text, "[[MEIMAD:VERIFICATION_HOOK]]"));
        Assert.Equal(1, Count(result.Text, "[[MEIMAD:CYCLE_START]]"));
        Assert.Equal(1, Count(result.Text, "[[MEIMAD:CYCLE_END]]"));
        Assert.Contains(result.Changes, change => change.Contains("legacy V1", StringComparison.Ordinal));
    }

    [Fact]
    public void Misplaced_hook_is_moved_before_the_first_executable_block()
    {
        var source = "O1500\nG90 G54 G0 X0 Y0 Z50.\n[[MEIMAD:VERIFICATION_HOOK]]\nM30\n";

        var result = Formatter.Apply(source, "HAAS_NGC");

        Assert.True(result.Validation.IsValid, result.Validation.Message);
        Assert.Equal(1, Count(result.Text, "[[MEIMAD:VERIFICATION_HOOK]]"));
        Assert.Contains(result.Changes, change => change.StartsWith("Moved", StringComparison.Ordinal));
    }

    [Fact]
    public void Duplicate_unique_placeholders_are_reported_and_left_for_the_programmer()
    {
        var formatted = Formatter.Apply(HaasProgram, "HAAS_NGC").Text;
        var duplicated = formatted.Replace(
            "[[MEIMAD:EVENT_CONTEXT]]\n", "[[MEIMAD:EVENT_CONTEXT]]\n[[MEIMAD:EVENT_CONTEXT]]\n", StringComparison.Ordinal);

        var result = Formatter.Apply(duplicated, "HAAS_NGC");

        Assert.False(result.Validation.IsValid);
        Assert.Equal("production_package_placeholder_duplicate", result.Validation.Code);
        Assert.Contains(result.Warnings, warning => warning.Contains("EVENT_CONTEXT", StringComparison.Ordinal));
    }

    [Fact]
    public void Missing_trailing_newline_is_preserved()
    {
        var result = Formatter.Apply("O1\nG0 X0 Y0 Z0\nM30", "HAAS_NGC");

        Assert.False(result.Text.EndsWith('\n'));
        Assert.EndsWith("[[MEIMAD:CYCLE_END]]\nM30", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_dialect_is_rejected()
    {
        var exception = Assert.Throws<GCodeValidationException>(() => Formatter.Apply(HaasProgram, "SIEMENS"));
        Assert.Equal("ncDialect", exception.Field);
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0;
             index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }
}
