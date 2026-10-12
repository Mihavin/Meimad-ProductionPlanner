using Meimad.Planner.Server.Application.Cnc;
using Meimad.Planner.Server.Application.GCode;

namespace Meimad.Planner.Server.Application.MachineEvidence;

/// <summary>
/// Reading, reviewing and reporting measured work offsets. Current = the VALID record of a Machine +
/// Case Operation + NC release + work offset; history = every record, newest measurement first. A
/// REVIEW_REQUIRED candidate becomes current only through <see cref="ReviewAsync"/>, which follows the
/// parallel-edit rule: the decision carries the candidate's version (and, when accepting, the current
/// record the reviewer compared with), and a stale decision is refused with an explanation.
/// </summary>
internal sealed class WorkOffsetService(
    IWorkOffsetRepository repository,
    ICncVerificationFoundationRepository? verification,
    TimeProvider timeProvider)
{
    internal const int DefaultLimit = 200;
    internal const int MaximumLimit = 2000;

    internal async Task<WorkOffsetOperationView?> ReadOperationAsync(
        string machineId, string caseOperationId, string? workOffset, int? limit, CancellationToken token = default)
    {
        Required(machineId, "machineId");
        Required(caseOperationId, "operationId");
        string? name = null;
        if (!string.IsNullOrWhiteSpace(workOffset))
        {
            var parsed = WorkOffsetNames.Parse(workOffset)
                ?? throw new WorkOffsetValidationException("invalid_work_offset",
                    "offset is a work offset name such as G54, G54.1 P1, G154 P1 or G15 H1.");
            // Haas G110-G129 are stored as G154 P1-P20.
            name = (parsed.Family == WorkOffsetFamily.HaasAlias
                ? new WorkOffsetName(WorkOffsetFamily.G154, parsed.Number - 109)
                : parsed).Canonical;
        }
        return await repository.ReadOperationAsync(machineId.Trim(), caseOperationId.Trim(), name, Limit(limit), token);
    }

    internal Task<WorkOffsetRecord?> GetAsync(string workOffsetId, CancellationToken token = default)
    {
        Required(workOffsetId, "workOffsetId");
        return repository.GetAsync(workOffsetId.Trim(), token);
    }

    internal Task<IReadOnlyList<WorkOffsetRecord>> ListAsync(
        string? statuses, string? machineId, string? caseOperationId, int? limit, CancellationToken token = default)
    {
        var list = (statuses ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(status => status.ToUpperInvariant())
            .Distinct()
            .ToArray();
        if (list.Any(status => !WorkOffsetStatuses.IsKnown(status)))
            throw new WorkOffsetValidationException("invalid_status",
                $"status is a comma-separated list of {string.Join(", ", WorkOffsetStatuses.All)}.");
        return repository.ListAsync(new WorkOffsetListQuery(
            list,
            string.IsNullOrWhiteSpace(machineId) ? null : machineId.Trim(),
            string.IsNullOrWhiteSpace(caseOperationId) ? null : caseOperationId.Trim(),
            Limit(limit)), token);
    }

    internal Task<WorkOffsetRecord> ReviewAsync(WorkOffsetReviewCommand command, CancellationToken token = default)
    {
        Required(command.WorkOffsetId, "workOffsetId");
        if (command.ExpectedVersion < 1)
            throw new WorkOffsetValidationException("version_required",
                "version is the version of the measurement you reviewed.");
        if (command.Comment is { Length: > 1000 })
            throw new WorkOffsetValidationException("comment_too_long", "comment is limited to 1000 characters.");
        return repository.ReviewAsync(command with { WorkOffsetId = command.WorkOffsetId.Trim() }, timeProvider.GetUtcNow(), token);
    }

    /// <summary>
    /// The Machine's work offset report program for one Case Operation (and NC release) in its NC dialect;
    /// null when the Machine or the Operation does not exist.
    /// </summary>
    internal async Task<NcWorkOffsetReport?> RenderReportAsync(
        string machineId, string caseOperationId, string? releaseId, string? offsets, string? axes,
        CancellationToken token = default)
    {
        Required(machineId, "machineId");
        Required(caseOperationId, "operationId");
        var context = await repository.GetMacroContextAsync(
            machineId.Trim(), caseOperationId.Trim(), string.IsNullOrWhiteSpace(releaseId) ? null : releaseId.Trim(), token);
        if (context is null) return null;
        var dialect = NcDialects.Profile(context.NcDialect);
        var names = new List<WorkOffsetName>();
        foreach (var text in (string.IsNullOrWhiteSpace(offsets) ? "G54" : offsets)
                     .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parsed = WorkOffsetNames.Parse(text);
            var canonical = parsed is null ? null : dialect.NormalizeWorkOffset(parsed);
            if (canonical is null)
                throw new WorkOffsetValidationException("unsupported_work_offset",
                    $"{text} is not a work offset of {dialect.DisplayName}.");
            var name = WorkOffsetNames.Parse(canonical)!;
            if (!names.Contains(name)) names.Add(name);
        }
        if (names.Count is 0 or > 12)
            throw new WorkOffsetValidationException("invalid_offsets", "offsets names 1 to 12 work offsets.");
        var axisList = NcWorkOffsetReports.ParseAxes(axes, haas: dialect.Id == NcDialects.HaasNgc);
        var sequenceVariable = dialect.DefaultEventSequenceVariable;
        if (dialect.Id == NcDialects.OkumaOsp
            && verification is not null
            && await verification.GetSettingsAsync(context.MachineId, token) is { EventSequenceVariable: { } configured })
            sequenceVariable = configured;
        var release = context.NcIdentityToken?.ToString(System.Globalization.CultureInfo.InvariantCulture)
            ?? (context.ReleaseId is null ? "NONE" : Safe(context.ReleaseId));
        return dialect.WorkOffsetReport(new NcWorkOffsetReportRequest(
            Safe(context.MachineNumber),
            context.OperationNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
            release,
            context.NcIdentityToken?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            names,
            axisList,
            sequenceVariable));
    }

    /// <summary>Letters, digits and hyphens only: what every control's print statement can carry.</summary>
    internal static string Safe(string value)
    {
        var safe = new string(value.ToUpperInvariant().Where(character => char.IsAsciiLetterOrDigit(character) || character == '-').ToArray());
        return safe.Length == 0 ? "0" : safe;
    }

    private static int Limit(int? value)
    {
        var limit = value ?? DefaultLimit;
        if (limit is < 1 or > MaximumLimit)
            throw new WorkOffsetValidationException("invalid_limit", $"limit is between 1 and {MaximumLimit}.");
        return limit;
    }

    private static void Required(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new WorkOffsetValidationException("validation_failed", $"{name} is required.");
    }
}
