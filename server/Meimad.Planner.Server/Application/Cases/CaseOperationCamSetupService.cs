using System.Globalization;
using System.Text.RegularExpressions;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Domain.Machines;

namespace Meimad.Planner.Server.Application.Cases;

/// <summary>
/// The CAM setup of one Case Operation (schema v113): the Work coordinate system — the NC program
/// zero of the Operation, the Cimatron UCS its NC procedures use — expressed in the part-model
/// coordinates that the Operation's linked fixture and rest-material files were exported in.
/// The orientation is R = Rz(z)·Ry(y)·Rx(x) in degrees; R's columns are the Work X/Y/Z axes in model
/// coordinates, and a model point p lies at Rᵀ(p − origin) in Work coordinates. Planner-entered CAM
/// context only: it changes no Case Operation version, planning data or Timeline.
/// </summary>
internal sealed record CaseOperationCamSetup(
    string CaseOperationId,
    string CaseId,
    string Source,
    string? WorkOffset,
    MachineVector3 OriginMm,
    MachineVector3 RotationDeg,
    string? Notes,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? UpdatedBy);

/// <summary>Validated values of a CAM setup write.</summary>
internal sealed record CaseOperationCamSetupValues(
    string Source,
    string? WorkOffset,
    MachineVector3 OriginMm,
    MachineVector3 RotationDeg,
    string? Notes);

/// <summary>A requested CAM setup before validation; omitted vectors and components are zero.</summary>
internal sealed record CaseOperationCamSetupInput(
    string? Source,
    string? WorkOffset,
    MachineVector3? OriginMm,
    MachineVector3? RotationDeg,
    string? Notes);

internal static class CaseOperationCamSetupSources
{
    internal const string Cimatron = "cimatron";
    internal const string Manual = "manual";
}

internal sealed record CaseOperationCamSetupIssue(string Field, string Code, string Message);

internal sealed class CaseOperationCamSetupValidationException(IReadOnlyList<CaseOperationCamSetupIssue> issues)
    : Exception("Operation CAM setup validation failed.")
{
    internal IReadOnlyList<CaseOperationCamSetupIssue> Issues { get; } = issues;
}

internal enum CaseOperationCamSetupOutcome
{
    Created,
    Updated,
    Deleted,
    OperationNotFound,
    SetupNotFound,
    AlreadyExists,
    Stale
}

/// <summary>
/// The outcome of a CAM setup write. <see cref="Setup"/> is the saved setup on success and the
/// current one when the write was refused because it was based on stale data.
/// </summary>
internal sealed record CaseOperationCamSetupWriteResult(CaseOperationCamSetupOutcome Outcome, CaseOperationCamSetup? Setup);

/// <summary>The setup of an Operation that belongs to the Case, or <c>OperationExists = false</c>.</summary>
internal sealed record CaseOperationCamSetupRead(bool OperationExists, CaseOperationCamSetup? Setup);

internal interface ICaseOperationCamSetupRepository
{
    Task<CaseOperationCamSetupRead> GetAsync(string caseId, string caseOperationId, CancellationToken cancellationToken);

    /// <summary>
    /// Creates (<paramref name="expectedVersion"/> null) or replaces the setup in one immediate
    /// transaction; a create over an existing setup or a replace of another version is refused.
    /// </summary>
    Task<CaseOperationCamSetupWriteResult> SaveAsync(
        string caseId,
        string caseOperationId,
        CaseOperationCamSetupValues values,
        int? expectedVersion,
        string actor,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<CaseOperationCamSetupWriteResult> DeleteAsync(
        string caseId,
        string caseOperationId,
        int expectedVersion,
        string actor,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}

internal sealed partial class CaseOperationCamSetupService(ICaseOperationCamSetupRepository repository, TimeProvider timeProvider)
{
    internal const double OriginLimitMm = 100000;
    internal const double RotationLimitDeg = 360;
    internal const int WorkOffsetMaximumLength = 16;
    internal const int NotesMaximumLength = 500;

    public Task<CaseOperationCamSetupRead> GetAsync(string caseId, string caseOperationId, CancellationToken cancellationToken) =>
        repository.GetAsync(caseId.Trim(), caseOperationId.Trim(), cancellationToken);

    public Task<CaseOperationCamSetupWriteResult> SaveAsync(
        string caseId,
        string caseOperationId,
        CaseOperationCamSetupInput input,
        int? expectedVersion,
        EditAuthority authority,
        CancellationToken cancellationToken)
    {
        var values = Validate(input);
        return repository.SaveAsync(
            caseId.Trim(), caseOperationId.Trim(), values, expectedVersion,
            SignedInActor.Require(authority), timeProvider.GetUtcNow(), cancellationToken);
    }

    public Task<CaseOperationCamSetupWriteResult> DeleteAsync(
        string caseId,
        string caseOperationId,
        int expectedVersion,
        EditAuthority authority,
        CancellationToken cancellationToken) =>
        repository.DeleteAsync(
            caseId.Trim(), caseOperationId.Trim(), expectedVersion,
            SignedInActor.Require(authority), timeProvider.GetUtcNow(), cancellationToken);

    internal static CaseOperationCamSetupValues Validate(CaseOperationCamSetupInput input)
    {
        var issues = new List<CaseOperationCamSetupIssue>();
        var source = input.Source?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(source))
            issues.Add(new("source", "required", "source is required: 'cimatron' or 'manual'."));
        else if (source is not (CaseOperationCamSetupSources.Cimatron or CaseOperationCamSetupSources.Manual))
            issues.Add(new("source", "invalid_source", "source must be exactly 'cimatron' or 'manual'."));

        string? workOffset = null;
        if (!string.IsNullOrWhiteSpace(input.WorkOffset))
        {
            workOffset = Spaces().Replace(input.WorkOffset.Trim().ToUpperInvariant(), " ");
            if (workOffset.Length > WorkOffsetMaximumLength || !WorkOffsetPattern().IsMatch(workOffset))
                issues.Add(new("workOffset", "invalid_work_offset",
                    "workOffset must be a work offset such as G54..G59, G54.1 P1, G154 P1, G110..G129 or G15 H1, or null."));
        }

        var origin = Vector(input.OriginMm, "originMm", OriginLimitMm, "mm", issues);
        var rotation = Vector(input.RotationDeg, "rotationDeg", RotationLimitDeg, "degrees", issues);

        var notes = string.IsNullOrWhiteSpace(input.Notes) ? null : input.Notes.Trim();
        if (notes is not null && notes.Length > NotesMaximumLength)
            issues.Add(new("notes", "too_long", $"notes may contain at most {NotesMaximumLength} characters."));

        if (issues.Count > 0) throw new CaseOperationCamSetupValidationException(issues);
        return new CaseOperationCamSetupValues(source!, workOffset, origin, rotation, notes);
    }

    private static MachineVector3 Vector(
        MachineVector3? value, string field, double limit, string unit, List<CaseOperationCamSetupIssue> issues)
    {
        if (value is null) return MachineVector3.Zero;
        foreach (var (axis, component) in new[] { ("x", value.X), ("y", value.Y), ("z", value.Z) })
        {
            if (!double.IsFinite(component))
                issues.Add(new($"{field}.{axis}", "not_finite", $"{field}.{axis} must be a finite number."));
            else if (Math.Abs(component) > limit)
                issues.Add(new($"{field}.{axis}", "out_of_range",
                    string.Create(CultureInfo.InvariantCulture, $"{field}.{axis} must be between -{limit} and {limit} {unit}.")));
        }
        return value;
    }

    // G54..G59; FANUC/Haas/Mazak extended G54.1 Pn; Haas G154 Pn and G110..G129; Okuma OSP G15 Hn.
    [GeneratedRegex(@"^(G5[4-9]|G54\.1 ?P[1-9][0-9]{0,2}|G154 ?P[1-9][0-9]{0,2}|G1[12][0-9]|G15 ?H[0-9]{1,3})$", RegexOptions.CultureInvariant)]
    private static partial Regex WorkOffsetPattern();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();
}
