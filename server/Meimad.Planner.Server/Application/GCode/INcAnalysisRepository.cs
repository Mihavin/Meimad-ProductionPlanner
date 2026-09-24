using Meimad.Planner.Server.Domain.GCode;

namespace Meimad.Planner.Server.Application.GCode;

internal sealed record NcAnalysisCandidate(
    string GCodeReleaseId,
    string PostprocessorId,
    string StoredRelativePath);

/// <summary>How one active Machine that supports a postprocessor interprets NC programs.</summary>
/// <param name="NcDialect">The Machine's NC dialect (HAAS_NGC, FANUC_MACRO_B, ...).</param>
/// <param name="NcViewerMachine">The Machine's NC viewer machine (engine definition id), or null for automatic detection.</param>
internal sealed record MachineNcInterpretation(string NcDialect, string? NcViewerMachine);

internal interface INcAnalysisRepository
{
    /// <summary>NC interpretation of every active Machine that supports the postprocessor (may repeat).</summary>
    Task<IReadOnlyList<MachineNcInterpretation>> ListPostprocessorMachineInterpretationsAsync(
        string postprocessorId,
        CancellationToken cancellationToken);

    /// <summary>Releases that have no analysis with <paramref name="parserVersion"/>, oldest first.</summary>
    Task<IReadOnlyList<NcAnalysisCandidate>> ListReleasesWithoutAnalysisAsync(
        string parserVersion,
        int limit,
        IReadOnlyCollection<string> excludedReleaseIds,
        CancellationToken cancellationToken);

    /// <summary>
    /// Stores an additional analysis for an existing immutable release and recalculates the
    /// per-Machine estimates from it. Returns false when that parser version already exists.
    /// </summary>
    Task<bool> AddAnalysisAsync(
        NcAnalysisCandidate release,
        NcProgramAnalysis analysis,
        CancellationToken cancellationToken);
}
