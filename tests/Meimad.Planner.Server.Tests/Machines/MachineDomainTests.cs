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
