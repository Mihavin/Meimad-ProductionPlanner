using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Application.Haas;
using Meimad.Planner.Server.Domain.GCode;
using Meimad.Planner.Server.Domain.Haas;

namespace Meimad.Planner.Server.Application.GCode;

internal sealed class GCodeService
{
    private readonly IGCodeRepository repository;
    private readonly GCodeArtifactStore artifactStore;
    private readonly TimeProvider timeProvider;
    private readonly INcHeaderParser headerParser;
    private readonly ILogger<GCodeService> logger;

    public GCodeService(
        IGCodeRepository repository,
        GCodeArtifactStore artifactStore,
        TimeProvider timeProvider,
        INcHeaderParser headerParser,
        ILogger<GCodeService> logger)
    {
        this.repository = repository;
        this.artifactStore = artifactStore;
        this.timeProvider = timeProvider;
        this.headerParser = headerParser;
        this.logger = logger;
    }

    internal async Task<OperationGCodeCatalog> ReadCatalogAsync(
        string caseId,
        string caseOperationId,
        CancellationToken cancellationToken = default) =>
        await repository.ReadCatalogAsync(RequiredId(caseId, "caseId"), RequiredId(caseOperationId, "caseOperationId"), cancellationToken)
        ?? throw new GCodeOperationNotFoundException(caseOperationId);

    internal async Task<GCodeRelease> ReleaseAsync(
        ReleaseGCodeCommand command,
        EditAuthority authority,
        CancellationToken cancellationToken = default)
    {
        var caseId = RequiredId(command.CaseId, "caseId");
        var operationId = RequiredId(command.CaseOperationId, "caseOperationId");
        var postprocessorId = RequiredId(command.PostprocessorId, "postprocessorId");
        var scope = RequiredScope(command.ChangeScope);
        var comment = RequiredText(command.ReleaseComment, "releaseComment", 2000);
        var processDescription = scope == GCodeChangeScopes.NewProcessRevision
            ? RequiredText(command.ProcessChangeDescription, "processChangeDescription", 2000)
            : OptionalText(command.ProcessChangeDescription, 2000) ?? comment;
        if (scope == GCodeChangeScopes.NewProcessRevision && !command.ConfirmNewProcessRevision)
        {
            throw new GCodeValidationException(
                "confirmNewProcessRevision",
                "confirmation_required",
                "Creating a new manufacturing-process revision requires explicit confirmation.");
        }

        if (!command.ConfirmToolTable)
        {
            throw new GCodeValidationException(
                "confirmToolTable",
                "confirmation_required",
                "The exact physical tool-table release must be confirmed.");
        }

        if (command.GCodeFile is null)
        {
            throw new GCodeValidationException("gCodeFile", "required", "A released G-code file is required.");
        }

        if (command.ReuseActiveToolTable && command.ToolTableFile is not null)
        {
            throw new GCodeValidationException(
                "toolTableFile",
                "conflicting_tool_table_choice",
                "Choose either the active tool table or a new uploaded tool table, not both.");
        }

        if (scope == GCodeChangeScopes.LocalPostRevision && command.ToolTableFile is not null)
        {
            throw new GCodeValidationException(
                "toolTableFile",
                "new_process_revision_required",
                "A physical tool-table change requires NEW_PROCESS_REVISION.");
        }

        var releaseId = Guid.NewGuid().ToString("N");
        var candidateProcessId = Guid.NewGuid().ToString("N");
        var gcodePublication = await artifactStore.PublishGCodeAsync(
            operationId, releaseId, command.GCodeFile, cancellationToken);
        StoredArtifactPublication? toolPublication = null;
        ReleasedToolTableDefinition? toolDefinition = null;
        try
        {
            if (command.ToolTableFile is not null)
            {
                toolPublication = await artifactStore.PublishToolTableAsync(
                    operationId,
                    Guid.NewGuid().ToString("N"),
                    command.ToolTableFile,
                    cancellationToken);
                toolDefinition = await ReleasedToolTableParser.ParseAsync(
                    artifactStore.ResolveStoredPath(toolPublication.File.StoredRelativePath),
                    toolPublication.File.OriginalFileName,
                    cancellationToken);
            }

            var releasedAt = timeProvider.GetUtcNow();
            var storedGCodePath = artifactStore.ResolveStoredPath(gcodePublication.File.StoredRelativePath);
            var verificationHook = NcVerificationHookParser.ParseRequired(
                File.ReadLines(storedGCodePath));
            NcHeaderMetadata headerMetadata;
            try
            {
                headerMetadata = headerParser.Parse(File.ReadLines(storedGCodePath).Take(50));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception,
                    "NC header parsing failed for released artifact {ReleaseId}.",
                    gcodePublication.File.ArtifactId);
                headerMetadata = new NcHeaderMetadata("HEADER_INVALID", null, null, null,
                    null, null, string.Empty, NcHeaderParser.CurrentVersion);
            }
            NcProgramAnalysis analysis;
            try
            {
                analysis = await NcProgramParser.ParseAsync(
                    storedGCodePath,
                    releasedAt,
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(
                    exception,
                    "NC analysis was unavailable for released artifact {ReleaseId}; the production release will be preserved.",
                    gcodePublication.File.ArtifactId);
                analysis = NcProgramParser.Unavailable(
                    releasedAt,
                    "Estimate unavailable: the NC parser could not interpret this release.");
            }
            var release = await repository.PublishAsync(new PublishGCodeReleaseCommand(
                caseId,
                operationId,
                postprocessorId,
                scope,
                comment,
                processDescription,
                command.ConfirmNewProcessRevision,
                command.ReuseActiveToolTable,
                command.ConfirmToolTable,
                candidateProcessId,
                gcodePublication.File,
                toolPublication?.File,
                toolDefinition,
                analysis,
                headerMetadata,
                verificationHook,
                releasedAt,
                command.ManufacturingProgramId,
                command.Outputs), authority, cancellationToken);
            logger.LogInformation(
                "Released G-code {ReleaseId} for Operation {OperationId}, Process Revision {ProcessRevisionNumber}, Postprocessor {PostprocessorId}, Post Revision {PostRevision}.",
                release.GCodeReleaseId,
                operationId,
                release.ProcessRevisionNumber,
                postprocessorId,
                release.PostSpecificRevision);
            return release;
        }
        catch
        {
            artifactStore.DeletePublication(gcodePublication);
            artifactStore.DeletePublication(toolPublication);
            throw;
        }
    }

    internal async Task<GCodeRelease> ReleaseForProgramAsync(
        string manufacturingProgramId,
        ReleaseGCodeCommand command,
        EditAuthority authority,
        CancellationToken cancellationToken = default)
    {
        var programId = RequiredId(manufacturingProgramId, "manufacturingProgramId");
        var requestedScope = RequiredScope(command.ChangeScope);
        if (requestedScope == GCodeChangeScopes.LocalPostRevision && command.Outputs is not null)
        {
            throw new GCodeValidationException(
                "outputsJson", "new_process_revision_required",
                "A local Post revision preserves the exact output recipe; changing outputs requires NEW_PROCESS_REVISION.");
        }
        IReadOnlyList<ManufacturingProgramRevisionOutput>? outputs = null;
        if (command.Outputs is not null)
        {
            var normalized = ManufacturingProgramService.ValidateOutputs(
                command.Outputs.Select(value => new ManufacturingProgramOutputInput(
                    value.CaseOperationId, value.QuantityPerCycle, value.DisplayOrder,
                    value.ExecutionMetadataJson)).ToArray());
            outputs = normalized.Select(value => new ManufacturingProgramRevisionOutput(
                Guid.NewGuid().ToString("N"), value.CaseOperationId!, value.QuantityPerCycle,
                value.DisplayOrder, value.ExecutionMetadataJson!)).ToArray();
        }

        var context = await repository.ResolveProgramPublicationContextAsync(
            programId, outputs, cancellationToken)
            ?? throw new ManufacturingProgramNotFoundException(programId);
        return await ReleaseAsync(command with
        {
            CaseId = context.CaseId,
            CaseOperationId = context.CaseOperationId,
            ManufacturingProgramId = programId,
            Outputs = outputs
        }, authority, cancellationToken);
    }

    internal async Task<ReleasedFileDownload> OpenReleaseFileAsync(
        string operationId,
        string releaseId,
        CancellationToken cancellationToken = default)
    {
        var file = await repository.ReadGCodeFileAsync(
            RequiredId(operationId, "caseOperationId"),
            RequiredId(releaseId, "releaseId"),
            cancellationToken) ?? throw new GCodeReleaseNotFoundException(releaseId);
        return await ResolveDownloadAsync(file, cancellationToken);
    }

    internal async Task<ReleasedFileDownload> OpenProgramReleaseFileAsync(
        string programId,
        string releaseId,
        CancellationToken cancellationToken = default)
    {
        var file = await repository.ReadProgramGCodeFileAsync(
            RequiredId(programId, "manufacturingProgramId"),
            RequiredId(releaseId, "releaseId"), cancellationToken)
            ?? throw new GCodeReleaseNotFoundException(releaseId);
        return await ResolveDownloadAsync(file, cancellationToken);
    }

    internal async Task<ReleasedFileDownload> OpenToolTableFileAsync(
        string operationId,
        string toolTableReleaseId,
        CancellationToken cancellationToken = default)
    {
        var file = await repository.ReadToolTableFileAsync(
            RequiredId(operationId, "caseOperationId"),
            RequiredId(toolTableReleaseId, "toolTableReleaseId"),
            cancellationToken) ?? throw new GCodeToolTableNotFoundException(toolTableReleaseId);
        return await ResolveDownloadAsync(file, cancellationToken);
    }

    internal async Task<ReleasedFileDownload> OpenProgramToolTableFileAsync(
        string programId,
        string toolTableReleaseId,
        CancellationToken cancellationToken = default)
    {
        var file = await repository.ReadProgramToolTableFileAsync(
            RequiredId(programId, "manufacturingProgramId"),
            RequiredId(toolTableReleaseId, "toolTableReleaseId"), cancellationToken)
            ?? throw new GCodeToolTableNotFoundException(toolTableReleaseId);
        return await ResolveDownloadAsync(file, cancellationToken);
    }

    private async Task<ReleasedFileDownload> ResolveDownloadAsync(
        StoredReleaseFile file,
        CancellationToken cancellationToken)
    {
        var absolute = artifactStore.ResolveStoredPath(file.StoredRelativePath);
        if (!File.Exists(absolute) || new FileInfo(absolute).Length != file.FileSize)
        {
            throw new GCodeFileUnavailableException(file.ArtifactId);
        }

        string actualHash;
        await using (var input = File.OpenRead(absolute))
        {
            actualHash = Convert.ToHexStringLower(
                await System.Security.Cryptography.SHA256.HashDataAsync(input, cancellationToken));
        }

        if (!string.Equals(actualHash, file.FileHash, StringComparison.Ordinal))
        {
            logger.LogError(
                "Released artifact {ArtifactId} failed SHA-256 verification.",
                file.ArtifactId);
            throw new GCodeFileUnavailableException(file.ArtifactId);
        }

        return new ReleasedFileDownload(absolute, file.OriginalFileName, file.FileHash, file.FileSize);
    }

    private static string RequiredId(string? value, string field)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > 200)
        {
            throw new GCodeValidationException(field, "required", $"{field} is required.");
        }

        return trimmed;
    }

    private static string RequiredScope(string? value)
    {
        var normalized = value?.Trim().ToUpperInvariant();
        if (!GCodeChangeScopes.IsSupported(normalized))
        {
            throw new GCodeValidationException(
                "changeScope",
                "invalid_change_scope",
                "changeScope must be LOCAL_POST_REVISION or NEW_PROCESS_REVISION.");
        }

        return normalized!;
    }

    private static string RequiredText(string? value, string field, int maximum)
    {
        var normalized = OptionalText(value, maximum);
        if (normalized is null)
        {
            throw new GCodeValidationException(field, "required", $"{field} is required.");
        }

        return normalized;
    }

    private static string? OptionalText(string? value, int maximum)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        if (normalized.Length > maximum)
        {
            throw new GCodeValidationException("text", "too_long", $"Text may contain at most {maximum} characters.");
        }

        return normalized;
    }
}

internal sealed class GCodeValidationException(string field, string code, string message) : Exception(message)
{
    internal string Field { get; } = field;
    internal string Code { get; } = code;
}

internal sealed class GCodeOperationNotFoundException(string id) : Exception($"Case Operation '{id}' was not found.");
internal sealed class GCodeReleaseNotFoundException(string id) : Exception($"G-code release '{id}' was not found.");
internal sealed class GCodeToolTableNotFoundException(string id) : Exception($"Tool-table release '{id}' was not found.");
internal sealed class GCodeFileUnavailableException(string id) : Exception($"Released artifact '{id}' is unavailable from configured storage.");
internal sealed class GCodePostprocessorNotFoundException(string id) : Exception($"Active Postprocessor '{id}' was not found.");
internal sealed class GCodeProcessStateException(string code, string message) : Exception(message)
{
    internal string Code { get; } = code;
}
