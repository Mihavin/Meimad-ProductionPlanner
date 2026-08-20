using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Domain.GCode;

namespace Meimad.Planner.Server.Application.GCode;

internal interface IGCodeRepository
{
    Task<OperationGCodeCatalog?> ReadCatalogAsync(
        string caseId,
        string caseOperationId,
        CancellationToken cancellationToken);

    Task<GCodeRelease> PublishAsync(
        PublishGCodeReleaseCommand command,
        EditAuthority authority,
        CancellationToken cancellationToken);

    Task<StoredReleaseFile?> ReadGCodeFileAsync(
        string caseOperationId,
        string releaseId,
        CancellationToken cancellationToken);

    Task<StoredReleaseFile?> ReadToolTableFileAsync(
        string caseOperationId,
        string toolTableReleaseId,
        CancellationToken cancellationToken);

    Task<IReadOnlySet<string>> ListStoredArtifactIdsAsync(CancellationToken cancellationToken);
}
