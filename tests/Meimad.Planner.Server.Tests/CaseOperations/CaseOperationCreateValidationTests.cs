using Meimad.Planner.Server.Domain.CaseOperations;

namespace Meimad.Planner.Server.Tests.CaseOperations;

public sealed class CaseOperationCreateValidationTests
{
    [Fact]
    public void Normalizes_complete_operation_and_preserves_dependency_semantics()
    {
        var values = CaseOperationValidator.ValidateAndNormalize(
            new CaseOperationCreateValues(
                " case-1 ",
                20,
                " Finish mill ",
                " fiveAxisMill ",
                1200,
                180,
                "LOCKED_SIMULTANEOUS",
                " operation-10 ",
                " group-a "));

        Assert.Equal("case-1", values.CaseId);
        Assert.Equal("Finish mill", values.Name);
        Assert.Equal("fiveAxisMill", values.RequiredMachineType);
        Assert.Equal(CaseOperationDependencyType.LockedSimultaneous, values.DependencyType);
        Assert.Equal("operation-10", values.PredecessorCaseOperationId);
        Assert.Equal("group-a", values.SimultaneousGroupKey);
    }

    [Theory]
    [InlineData("INDEPENDENT", "operation-10", null, "predecessor_not_allowed")]
    [InlineData("SEQUENTIAL", null, null, "predecessor_required")]
    [InlineData("PARALLEL_CAPABLE", null, null, "predecessor_required")]
    [InlineData("LOCKED_SIMULTANEOUS", "operation-10", null, "simultaneous_group_required")]
    [InlineData("SEQUENTIAL", "operation-10", "group-a", "simultaneous_group_not_allowed")]
    public void Rejects_dependency_shapes_that_conflict_with_declared_semantics(
        string dependencyType,
        string? predecessorId,
        string? groupKey,
        string expectedCode)
    {
        var exception = Assert.Throws<CaseOperationValidationException>(() =>
            CaseOperationValidator.ValidateAndNormalize(
                new CaseOperationCreateValues(
                    "case-1",
                    20,
                    "Finish mill",
                    null,
                    10,
                    20,
                    dependencyType,
                    predecessorId,
                    groupKey)));

        Assert.Contains(exception.Issues, issue => issue.Code == expectedCode);
    }
}
