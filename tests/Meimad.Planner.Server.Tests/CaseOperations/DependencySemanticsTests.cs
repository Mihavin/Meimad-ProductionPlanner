using Meimad.Planner.Server.Domain.CaseOperations;

namespace Meimad.Planner.Server.Tests.CaseOperations;

public sealed class DependencySemanticsTests
{
    public static TheoryData<
        int,
        bool,
        bool,
        bool,
        bool,
        bool,
        bool,
        bool,
        bool> Semantics => new()
    {
        { (int)CaseOperationDependencyType.Sequential, true, false, false, false, false, false, false, false },
        { (int)CaseOperationDependencyType.ParallelCapable, false, true, true, false, false, false, false, false },
        { (int)CaseOperationDependencyType.Independent, false, true, false, true, false, false, false, false },
        { (int)CaseOperationDependencyType.LockedSimultaneous, false, true, false, false, true, true, true, true }
    };

    public static TheoryData<int, string> ContractTokens => new()
    {
        { (int)CaseOperationDependencyType.Sequential, "SEQUENTIAL" },
        { (int)CaseOperationDependencyType.ParallelCapable, "PARALLEL_CAPABLE" },
        { (int)CaseOperationDependencyType.Independent, "INDEPENDENT" },
        { (int)CaseOperationDependencyType.LockedSimultaneous, "LOCKED_SIMULTANEOUS" }
    };

    [Theory]
    [MemberData(nameof(Semantics))]
    public void Dependency_type_exposes_required_rule_semantics(
        int typeValue,
        bool createsOrderingConstraint,
        bool permitsOverlap,
        bool allowsPlannerChosenSequentialExecution,
        bool hasNoTimingOrOrderingRelationship,
        bool requiresEqualStartAndFinish,
        bool usesLongestMemberDuration,
        bool reservesEveryMemberUntilGroupFinish,
        bool usesSimultaneousGroup)
    {
        var type = (CaseOperationDependencyType)typeValue;
        var semantics = type.GetSemantics();

        Assert.Equal(createsOrderingConstraint, semantics.CreatesOrderingConstraint);
        Assert.Equal(permitsOverlap, semantics.PermitsOverlap);
        Assert.Equal(
            allowsPlannerChosenSequentialExecution,
            semantics.AllowsPlannerChosenSequentialExecution);
        Assert.Equal(
            hasNoTimingOrOrderingRelationship,
            semantics.HasNoTimingOrOrderingRelationship);
        Assert.Equal(requiresEqualStartAndFinish, semantics.RequiresEqualStartAndFinish);
        Assert.Equal(usesLongestMemberDuration, semantics.UsesLongestMemberDuration);
        Assert.Equal(
            reservesEveryMemberUntilGroupFinish,
            semantics.ReservesEveryMemberUntilGroupFinish);
        Assert.Equal(usesSimultaneousGroup, semantics.UsesSimultaneousGroup);
    }

    [Theory]
    [MemberData(nameof(ContractTokens))]
    public void Dependency_type_round_trips_exact_contract_token(
        int typeValue,
        string token)
    {
        var type = (CaseOperationDependencyType)typeValue;
        Assert.Equal(token, type.ToContractToken());
        Assert.True(CaseOperationDependencyTypes.TryParseContractToken(token, out var parsed));
        Assert.Equal(type, parsed);
    }

    [Theory]
    [InlineData("sequential")]
    [InlineData("PARALLEL-CAPABLE")]
    [InlineData("")]
    [InlineData(null)]
    public void Unknown_or_noncanonical_contract_token_is_rejected(string? token)
    {
        Assert.False(CaseOperationDependencyTypes.TryParseContractToken(token, out _));
    }
}
