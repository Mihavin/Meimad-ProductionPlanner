using Meimad.Planner.Server.Domain.Machines;

namespace Meimad.Planner.Server.Tests.Machines;

public sealed class MachineDomainTests
{
    [Fact]
    public void Validation_normalizes_master_data_and_rejects_duplicate_capabilities()
    {
        var values = MachineValidator.ValidateAndNormalize(new MachineValues(
            " M-07 ",
            " Five-axis mill ",
            " milling ",
            " fiveAxis ",
            [" probe ", "highSpeed"],
            " calendar-1 ",
            true,
            false,
            @" C:\Factory\MachinePictures\M-07.png "));

        Assert.Equal("M-07", values.Number);
        Assert.Equal("milling", values.ProcessType);
        Assert.Equal("fiveAxis", values.AxisType);
        Assert.Equal(["probe", "highSpeed"], values.Capabilities);
        Assert.Equal(@"C:\Factory\MachinePictures\M-07.png", values.PicturePath);

        var exception = Assert.Throws<MachineValidationException>(() =>
            MachineValidator.ValidateAndNormalize(new MachineValues(
                "M-08",
                "Duplicate",
                "milling",
                null,
                ["probe", "PROBE"],
                "calendar-1",
                true,
                true)));
        Assert.Contains(exception.Issues, issue => issue.Code == "duplicate_capability");

        var invalidPath = Assert.Throws<MachineValidationException>(() =>
            MachineValidator.ValidateAndNormalize(new MachineValues(
                "M-09",
                "Relative picture",
                "milling",
                null,
                [],
                "calendar-1",
                true,
                true,
                @"pictures\M-09.png")));
        Assert.Contains(invalidPath.Issues, issue =>
            issue.Field == "picturePath" && issue.Code == "absolute_path_required");

        var invalidCharacter = Assert.Throws<MachineValidationException>(() =>
            MachineValidator.ValidateAndNormalize(new MachineValues(
                "M-10",
                "Invalid picture path",
                "milling",
                null,
                [],
                "calendar-1",
                true,
                true,
                "C:\\Factory\\MachinePictures\\M-10\0.png")));
        Assert.Contains(invalidCharacter.Issues, issue =>
            issue.Field == "picturePath" && issue.Code == "invalid_path");
    }

    [Fact]
    public void Compatibility_matches_process_axis_or_capability_and_requires_active_machine()
    {
        var machine = Machine(
            processType: "milling",
            axisType: "fiveAxis",
            capabilities: ["probe", "laser"]);

        Assert.True(MachineCompatibility.IsCompatible(machine, null));
        Assert.True(MachineCompatibility.IsCompatible(machine, "MILLING"));
        Assert.True(MachineCompatibility.IsCompatible(machine, "fiveaxis"));
        Assert.True(MachineCompatibility.IsCompatible(machine, "LASER"));
        Assert.False(MachineCompatibility.IsCompatible(machine, "turning"));
        Assert.False(MachineCompatibility.IsCompatible(machine with { IsActive = false }, "milling"));
    }

    [Fact]
    public void Manual_machine_does_not_require_a_postprocessor()
    {
        var machine = Machine("manual", null, []) with
        {
            ExecutionMode = MachineExecutionModes.Manual,
            SupportedPostprocessorIds = []
        };

        Assert.False(MachinePostprocessorCompatibility.RequiresReleasedGCode(machine));
    }

    [Fact]
    public void Cnc_machine_supports_only_explicitly_mapped_postprocessors()
    {
        var machine = Machine("milling", "fourAxis", []) with
        {
            ExecutionMode = MachineExecutionModes.CncGCode,
            SupportedPostprocessorIds = ["post-doosan-3x", "post-doosan-4x"]
        };

        Assert.True(MachinePostprocessorCompatibility.RequiresReleasedGCode(machine));
        Assert.True(MachinePostprocessorCompatibility.Supports(machine, "post-doosan-3x"));
        Assert.True(MachinePostprocessorCompatibility.Supports(machine, "post-doosan-4x"));
        Assert.False(MachinePostprocessorCompatibility.Supports(machine, "post-haas-umc"));
    }

    [Fact]
    public void Execution_capacity_and_timing_values_are_validated_with_neutral_factor_default()
    {
        var valid = MachineValidator.ValidateAndNormalize(new MachineValues(
            "M-11", "CNC", "milling", null, [], "calendar-1", true, true,
            ExecutionMode: MachineExecutionModes.CncGCode,
            SupportedPostprocessorIds: ["post-1"],
            UsableToolPositions: 30,
            RapidRateMillimetersPerMinute: 24_000,
            ToolChangeTimeSeconds: 0));

        Assert.Equal(1.0, valid.MachineTimeFactor);
        Assert.Equal(30, valid.UsableToolPositions);

        var invalid = Assert.Throws<MachineValidationException>(() =>
            MachineValidator.ValidateAndNormalize(new MachineValues(
                "M-12", "Invalid", "milling", null, [], "calendar-1", true, true,
                ExecutionMode: MachineExecutionModes.CncGCode,
                UsableToolPositions: 0,
                RapidRateMillimetersPerMinute: -1,
                ToolChangeTimeSeconds: -1,
                MachineTimeFactor: 0)));

        Assert.Contains(invalid.Issues, issue => issue.Field == "usableToolPositions");
        Assert.Contains(invalid.Issues, issue => issue.Field == "rapidRateMillimetersPerMinute");
        Assert.Contains(invalid.Issues, issue => issue.Field == "toolChangeTimeSeconds");
        Assert.Contains(invalid.Issues, issue => issue.Field == "machineTimeFactor");
    }

    private static Machine Machine(
        string processType,
        string? axisType,
        IReadOnlyList<string> capabilities) => new(
        "machine-1",
        "M-01",
        "Machine 1",
        processType,
        axisType,
        capabilities,
        "calendar-1",
        true,
        true,
        null,
        0,
        1,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow);
}
