namespace Meimad.Planner.Server.Domain.CaseOperations;

internal enum CaseOperationDependencyType
{
    Sequential,
    ParallelCapable,
    Independent,
    LockedSimultaneous
}

internal sealed record CaseOperationDependencySemantics(
    bool CreatesOrderingConstraint,
    bool PermitsOverlap,
    bool AllowsPlannerChosenSequentialExecution,
    bool HasNoTimingOrOrderingRelationship,
    bool RequiresEqualStartAndFinish,
    bool UsesLongestMemberDuration,
    bool ReservesEveryMemberUntilGroupFinish,
    bool UsesSimultaneousGroup);

internal static class CaseOperationDependencyTypes
{
    internal const string SequentialToken = "SEQUENTIAL";
    internal const string ParallelCapableToken = "PARALLEL_CAPABLE";
    internal const string IndependentToken = "INDEPENDENT";
    internal const string LockedSimultaneousToken = "LOCKED_SIMULTANEOUS";

    internal static CaseOperationDependencySemantics GetSemantics(
        this CaseOperationDependencyType type) => type switch
        {
            CaseOperationDependencyType.Sequential => new(
                CreatesOrderingConstraint: true,
                PermitsOverlap: false,
                AllowsPlannerChosenSequentialExecution: false,
                HasNoTimingOrOrderingRelationship: false,
                RequiresEqualStartAndFinish: false,
                UsesLongestMemberDuration: false,
                ReservesEveryMemberUntilGroupFinish: false,
                UsesSimultaneousGroup: false),
            CaseOperationDependencyType.ParallelCapable => new(
                CreatesOrderingConstraint: false,
                PermitsOverlap: true,
                AllowsPlannerChosenSequentialExecution: true,
                HasNoTimingOrOrderingRelationship: false,
                RequiresEqualStartAndFinish: false,
                UsesLongestMemberDuration: false,
                ReservesEveryMemberUntilGroupFinish: false,
                UsesSimultaneousGroup: false),
            CaseOperationDependencyType.Independent => new(
                CreatesOrderingConstraint: false,
                PermitsOverlap: true,
                AllowsPlannerChosenSequentialExecution: false,
                HasNoTimingOrOrderingRelationship: true,
                RequiresEqualStartAndFinish: false,
                UsesLongestMemberDuration: false,
                ReservesEveryMemberUntilGroupFinish: false,
                UsesSimultaneousGroup: false),
            CaseOperationDependencyType.LockedSimultaneous => new(
                CreatesOrderingConstraint: false,
                PermitsOverlap: true,
                AllowsPlannerChosenSequentialExecution: false,
                HasNoTimingOrOrderingRelationship: false,
                RequiresEqualStartAndFinish: true,
                UsesLongestMemberDuration: true,
                ReservesEveryMemberUntilGroupFinish: true,
                UsesSimultaneousGroup: true),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
        };

    internal static string ToContractToken(this CaseOperationDependencyType type) =>
        type switch
        {
            CaseOperationDependencyType.Sequential => SequentialToken,
            CaseOperationDependencyType.ParallelCapable => ParallelCapableToken,
            CaseOperationDependencyType.Independent => IndependentToken,
            CaseOperationDependencyType.LockedSimultaneous => LockedSimultaneousToken,
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
        };

    internal static bool TryParseContractToken(
        string? token,
        out CaseOperationDependencyType type)
    {
        type = token switch
        {
            SequentialToken => CaseOperationDependencyType.Sequential,
            ParallelCapableToken => CaseOperationDependencyType.ParallelCapable,
            IndependentToken => CaseOperationDependencyType.Independent,
            LockedSimultaneousToken => CaseOperationDependencyType.LockedSimultaneous,
            _ => default
        };

        return token is SequentialToken
            or ParallelCapableToken
            or IndependentToken
            or LockedSimultaneousToken;
    }
}
