using Meimad.Planner.Server.Domain.CaseOperations;

namespace Meimad.Planner.Server.Tests.CaseOperations;

public sealed class CaseOperationGraphTests
{
    [Fact]
    public void Valid_graph_accepts_all_four_dependency_types()
    {
        var operations = Operations("A", "B", "C", "D", "E");
        var dependencies = new[]
        {
            Dependency("independent", CaseOperationDependencyType.Independent, "A", "C"),
            Dependency("sequential", CaseOperationDependencyType.Sequential, "A", "B"),
            Dependency("parallel", CaseOperationDependencyType.ParallelCapable, "B", "C"),
            Dependency(
                "locked",
                CaseOperationDependencyType.LockedSimultaneous,
                "D",
                "E",
                "sim-group-1")
        };

        var graph = CaseOperationGraph.Create("case-1", operations, dependencies);

        Assert.Equal(5, graph.Operations.Count);
        Assert.Equal(4, graph.Dependencies.Count);
        Assert.Equal(["A"], graph.GetSequentialPrerequisiteIds("B"));
        Assert.True(graph.GetLockedSimultaneousGroupMembers("sim-group-1").SetEquals(["D", "E"]));
    }

    [Theory]
    [InlineData("missing", "B")]
    [InlineData("A", "missing")]
    public void Dependency_rejects_reference_outside_case_graph(string fromId, string toId)
    {
        var exception = Assert.Throws<CaseOperationGraphValidationException>(() =>
            CaseOperationGraph.Create(
                "case-1",
                Operations("A", "B"),
                [Dependency("invalid", CaseOperationDependencyType.Sequential, fromId, toId)]));

        Assert.Contains(exception.Issues, issue => issue.Code == "invalid_reference");
    }

    [Fact]
    public void Graph_rejects_operation_from_another_case()
    {
        var operations = new[]
        {
            Operation("A", 0),
            Operation("B", 1) with { CaseId = "case-2" }
        };

        var exception = Assert.Throws<CaseOperationGraphValidationException>(() =>
            CaseOperationGraph.Create("case-1", operations, []));

        Assert.Contains(exception.Issues, issue => issue.Code == "case_mismatch");
    }

    [Fact]
    public void Dependency_rejects_self_reference()
    {
        var exception = Assert.Throws<CaseOperationGraphValidationException>(() =>
            CaseOperationGraph.Create(
                "case-1",
                Operations("A"),
                [Dependency("self", CaseOperationDependencyType.Sequential, "A", "A")]));

        Assert.Contains(exception.Issues, issue => issue.Code == "self_reference");
    }

    [Fact]
    public void Sequential_dependencies_reject_directed_cycle()
    {
        var exception = Assert.Throws<CaseOperationGraphValidationException>(() =>
            CaseOperationGraph.Create(
                "case-1",
                Operations("A", "B", "C"),
                [
                    Dependency("A-B", CaseOperationDependencyType.Sequential, "A", "B"),
                    Dependency("B-C", CaseOperationDependencyType.Sequential, "B", "C"),
                    Dependency("C-A", CaseOperationDependencyType.Sequential, "C", "A")
                ]));

        Assert.Contains(exception.Issues, issue => issue.Code == "sequential_cycle");
    }

    [Fact]
    public void Parallel_capable_relationship_cycle_does_not_create_ordering_cycle()
    {
        var graph = CaseOperationGraph.Create(
            "case-1",
            Operations("A", "B", "C"),
            [
                Dependency("A-B", CaseOperationDependencyType.ParallelCapable, "A", "B"),
                Dependency("B-C", CaseOperationDependencyType.ParallelCapable, "B", "C"),
                Dependency("C-A", CaseOperationDependencyType.ParallelCapable, "C", "A")
            ]);

        Assert.Equal(3, graph.Dependencies.Count);
    }

    [Fact]
    public void Sequential_cycle_through_locked_group_is_rejected()
    {
        var exception = Assert.Throws<CaseOperationGraphValidationException>(() =>
            CaseOperationGraph.Create(
                "case-1",
                Operations("A", "B", "C"),
                [
                    Dependency(
                        "A-B-locked",
                        CaseOperationDependencyType.LockedSimultaneous,
                        "A",
                        "B",
                        "group-1"),
                    Dependency("B-C", CaseOperationDependencyType.Sequential, "B", "C"),
                    Dependency("C-A", CaseOperationDependencyType.Sequential, "C", "A")
                ]));

        Assert.Contains(exception.Issues, issue => issue.Code == "sequential_cycle");
    }

    [Fact]
    public void Sequential_order_within_locked_group_is_rejected()
    {
        var exception = Assert.Throws<CaseOperationGraphValidationException>(() =>
            CaseOperationGraph.Create(
                "case-1",
                Operations("A", "B"),
                [
                    Dependency(
                        "locked",
                        CaseOperationDependencyType.LockedSimultaneous,
                        "A",
                        "B",
                        "group-1"),
                    Dependency("ordered", CaseOperationDependencyType.Sequential, "A", "B")
                ]));

        Assert.Contains(
            exception.Issues,
            issue => issue.Code == "locked_group_ordering_conflict");
    }

    [Fact]
    public void Locked_simultaneous_requires_group_key()
    {
        var exception = Assert.Throws<CaseOperationGraphValidationException>(() =>
            CaseOperationGraph.Create(
                "case-1",
                Operations("A", "B"),
                [Dependency("locked", CaseOperationDependencyType.LockedSimultaneous, "A", "B")]));

        Assert.Contains(
            exception.Issues,
            issue => issue.Code == "simultaneous_group_required");
    }

    [Fact]
    public void Operation_cannot_belong_to_multiple_locked_groups()
    {
        var exception = Assert.Throws<CaseOperationGraphValidationException>(() =>
            CaseOperationGraph.Create(
                "case-1",
                Operations("A", "B", "C"),
                [
                    Dependency(
                        "group-one",
                        CaseOperationDependencyType.LockedSimultaneous,
                        "A",
                        "B",
                        "group-1"),
                    Dependency(
                        "group-two",
                        CaseOperationDependencyType.LockedSimultaneous,
                        "A",
                        "C",
                        "group-2")
                ]));

        Assert.Contains(
            exception.Issues,
            issue => issue.Code == "multiple_simultaneous_groups");
    }

    [Fact]
    public void One_operation_pair_cannot_have_conflicting_meanings()
    {
        var exception = Assert.Throws<CaseOperationGraphValidationException>(() =>
            CaseOperationGraph.Create(
                "case-1",
                Operations("A", "B"),
                [
                    Dependency("parallel", CaseOperationDependencyType.ParallelCapable, "A", "B"),
                    Dependency("independent", CaseOperationDependencyType.Independent, "A", "B")
                ]));

        Assert.Contains(exception.Issues, issue => issue.Code == "conflicting_relationship");
    }

    [Fact]
    public void Invalid_operation_identity_and_route_values_are_rejected()
    {
        var now = DateTimeOffset.UtcNow;
        var operations = new[]
        {
            new CaseOperation("A", "case-1", 10, 0, "", null, -1, -1, 0, now, now.AddMinutes(-1)),
            Operation("A", 0)
        };

        var exception = Assert.Throws<CaseOperationGraphValidationException>(() =>
            CaseOperationGraph.Create("case-1", operations, []));

        Assert.Contains(exception.Issues, issue => issue.Code == "duplicate_operation_id");
        Assert.Contains(exception.Issues, issue => issue.Code == "duplicate_operation_number");
        Assert.Contains(exception.Issues, issue => issue.Code == "duplicate_route_position");
        Assert.Contains(exception.Issues, issue => issue.Code == "non_negative_required");
        Assert.Contains(exception.Issues, issue => issue.Code == "timestamp_order_invalid");
    }

    private static CaseOperation[] Operations(params string[] operationIds) =>
        operationIds.Select((id, index) => Operation(id, index)).ToArray();

    private static CaseOperation Operation(string operationId, int routePosition)
    {
        var now = DateTimeOffset.UtcNow;
        return new CaseOperation(
            operationId,
            "case-1",
            (routePosition + 1) * 10,
            routePosition,
            $"Operation {operationId}",
            "mill",
            60,
            30,
            1,
            now,
            now);
    }

    private static CaseOperationDependency Dependency(
        string dependencyId,
        CaseOperationDependencyType type,
        string from,
        string to,
        string? groupKey = null) =>
        new(dependencyId, type, from, to, groupKey);
}
