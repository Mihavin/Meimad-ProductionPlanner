namespace Meimad.Planner.Server.Domain.Projections;

internal sealed record ProjectionFreshness(string Revision, string RuntimeId, DateTimeOffset EvaluationAt,
    DateTimeOffset ComputedAt, int Attempts, string Status = "revision-stable");
