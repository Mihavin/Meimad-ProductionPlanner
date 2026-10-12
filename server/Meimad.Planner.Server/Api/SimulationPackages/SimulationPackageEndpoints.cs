using System.Text.Json;
using System.Text.Json.Serialization;
using Meimad.Planner.Server.Application.Accounts;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Application.SimulationPackages;
using Meimad.Planner.Server.Persistence;
using Meimad.Planner.SimulationPackage.Validation;

namespace Meimad.Planner.Server.Api.SimulationPackages;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ValidateSimulationPackageRequest(string? PackagePath, string? CaseId = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ImportSimulationPackageRequest(
    string? PackagePath,
    int? ExpectedCamSetupVersion,
    string? ExpectedCurrentImportId = null,
    string? SelectedStockPath = null,
    string? MachineId = null,
    bool RememberMachineMapping = false,
    bool OverwriteManualWorkCs = false);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record AddMachineMappingRequest(string? MatchField, string? MatchValue, string? MachineId);

internal sealed record SimulationPackageImportFileResponse(
    string Role, string Path, string Sha256, long SizeBytes, string? StockKind, string? SourceOperation,
    string? Name, bool? Collision, bool Selected, string? CaseModelFileId);

internal sealed record SimulationPackageImportResponse(
    string ImportId,
    string? CaseId,
    string? CaseOperationId,
    string Status,
    string? PackageId,
    string ManifestSha256,
    int? SchemaVersion,
    string PackagePath,
    string? ExporterName,
    string? ExporterVersion,
    string? CimatronVersion,
    string? SourceApplication,
    string? SourceProject,
    string? NcFileName,
    string? ExportedAt,
    string? OperationNumber,
    string? OperationName,
    int? ProcedureId,
    string? ProcedureName,
    string? MachineExternalId,
    string? MachineName,
    string? Postprocessor,
    string? AxisConfiguration,
    string? MachineId,
    string? MachineMatchedBy,
    string? Units,
    JsonElement? CoordinateSystem,
    string? SelectedStockPath,
    string? NcSha256,
    string? ToolTableSha256,
    IReadOnlyList<int> ToolNumbers,
    string? MatchingGcodeReleaseId,
    DateTimeOffset ImportedAt,
    string ImportedBy,
    DateTimeOffset? SupersededAt,
    string? SupersededByImportId,
    IReadOnlyList<SimulationPackageImportFileResponse> Files,
    JsonElement Report)
{
    internal static SimulationPackageImportResponse From(SimulationPackageImport item) => new(
        item.ImportId, item.CaseId, item.CaseOperationId, item.Status, item.PackageId, item.ManifestSha256, item.SchemaVersion,
        item.PackagePath, item.ExporterName, item.ExporterVersion, item.CimatronVersion, item.SourceApplication, item.SourceProject,
        item.NcFileName, item.ExportedAt, item.OperationNumber, item.OperationName, item.ProcedureId, item.ProcedureName,
        item.MachineExternalId, item.MachineName, item.Postprocessor, item.AxisConfiguration, item.MachineId, item.MachineMatchedBy,
        item.Units, item.CoordinateSystemJson is null ? null : JsonDocument.Parse(item.CoordinateSystemJson).RootElement.Clone(),
        item.SelectedStockPath, item.NcSha256, item.ToolTableSha256, item.ToolNumbers, item.MatchingGcodeReleaseId,
        item.ImportedAt, item.ImportedBy, item.SupersededAt, item.SupersededByImportId,
        item.Files.Select(file => new SimulationPackageImportFileResponse(file.Role, file.RelativePath, file.Sha256, file.SizeBytes,
            file.StockKind, file.SourceOperation, file.Name, file.Collision, file.Selected, file.CaseModelFileId)).ToArray(),
        JsonDocument.Parse(item.ReportJson).RootElement.Clone());
}

internal sealed record SimulationPackageImportListResponse(IReadOnlyList<SimulationPackageImportResponse> Items);

internal sealed record SimulationPackageMachineMappingResponse(
    string MappingId, string MatchField, string MatchValue, string MachineId, string? MachineNumber, string? MachineName,
    DateTimeOffset CreatedAt, string CreatedBy);

/// <summary>
/// Cimatron simulation packages (schema v117). Every signed-in user may validate a package folder and
/// read import reports; importing needs <c>cases.edit</c> (plus <c>setup.manage</c> to remember a
/// Machine mapping); the Machine mapping table needs <c>setup.manage</c>. An import carries the CAM
/// setup version and the Operation's current package it was based on and is refused with 409 when
/// either changed meanwhile; a blocked import answers 422 with the full report and changes nothing.
/// </summary>
internal static class SimulationPackageEndpoints
{
    internal static void MapSimulationPackageEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var packages = endpoints.MapGroup("/api/v1/simulation-packages");
        packages.MapPost("/validate", ValidateAsync);
        packages.MapGet(string.Empty, ListAsync);
        packages.MapGet("/machine-mappings", ListMappingsAsync);
        packages.MapPost("/machine-mappings", AddMappingAsync);
        packages.MapDelete("/machine-mappings/{mappingId}", DeleteMappingAsync);
        packages.MapGet("/{importId}", GetAsync);

        var operation = endpoints.MapGroup("/api/v1/cases/{caseId}/operations/{operationId}/simulation-packages");
        operation.MapGet(string.Empty, ListForOperationAsync);
        operation.MapPost(string.Empty, ImportAsync);
    }

    private static async Task<IResult> ValidateAsync(
        ValidateSimulationPackageRequest request,
        HttpContext context,
        SimulationPackageImportService service,
        SqliteSimulationPackageRepository repository,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.PackagePath))
            return PlanningHttpSupport.Error(StatusCodes.Status422UnprocessableEntity, "validation_failed", "packagePath is required.", context);
        string? workingFolder = null;
        if (!string.IsNullOrWhiteSpace(request.CaseId))
        {
            var packageCase = await repository.GetCaseAsync(request.CaseId.Trim(), cancellationToken);
            if (packageCase is null)
                return PlanningHttpSupport.Error(StatusCodes.Status404NotFound, "resource_not_found", "The requested Case was not found.", context);
            workingFolder = packageCase.WorkingFolder;
        }

        var (report, _) = await service.ValidateCoreAsync(request.PackagePath, workingFolder, cancellationToken);
        return Results.Ok(new { report = SimulationPackageReport.From(report, [], [], null, null) });
    }

    private static async Task<IResult> ListAsync(
        string? caseId,
        string? caseOperationId,
        string? status,
        int? limit,
        SqliteSimulationPackageRepository repository,
        CancellationToken cancellationToken)
    {
        var items = await repository.ListAsync(Blank(caseId), Blank(caseOperationId), Blank(status), limit ?? 100, cancellationToken);
        return Results.Ok(new SimulationPackageImportListResponse(items.Select(SimulationPackageImportResponse.From).ToArray()));
    }

    private static async Task<IResult> ListForOperationAsync(
        string caseId,
        string operationId,
        SqliteSimulationPackageRepository repository,
        CancellationToken cancellationToken)
    {
        var items = await repository.ListAsync(caseId, operationId, null, 50, cancellationToken);
        return Results.Ok(new SimulationPackageImportListResponse(items.Select(SimulationPackageImportResponse.From).ToArray()));
    }

    private static async Task<IResult> GetAsync(string importId, HttpContext context, SqliteSimulationPackageRepository repository, CancellationToken cancellationToken)
    {
        var item = await repository.GetAsync(importId, cancellationToken);
        return item is null
            ? PlanningHttpSupport.Error(StatusCodes.Status404NotFound, "resource_not_found", "The simulation package import was not found.", context)
            : Results.Ok(SimulationPackageImportResponse.From(item));
    }

    private static async Task<IResult> ImportAsync(
        string caseId,
        string operationId,
        ImportSimulationPackageRequest request,
        HttpContext context,
        SimulationPackageImportService service,
        IAccountRepository accounts,
        CancellationToken cancellationToken)
    {
        if (!PlanningHttpSupport.TryAuthorizeEdit(context, Permissions.EditCases, out var authority, out var error)) return error!;
        if (request.RememberMachineMapping
            && !PlanningHttpSupport.TryAuthorizeEdit(context, Permissions.ManageSetup, out _, out var setupError))
        {
            return setupError!;
        }

        if (string.IsNullOrWhiteSpace(request.PackagePath))
            return PlanningHttpSupport.Error(StatusCodes.Status422UnprocessableEntity, "validation_failed", "packagePath is required.", context);
        if (request.ExpectedCamSetupVersion is null or < 0)
        {
            return PlanningHttpSupport.Error(StatusCodes.Status428PreconditionRequired, "precondition_required",
                "expectedCamSetupVersion is required: the Operation CAM setup version you saw, or 0 when it had none.", context);
        }

        string actor;
        try
        {
            actor = SignedInActor.Require(authority!);
        }
        catch (EditModeMutationException exception)
        {
            return PlanningHttpSupport.Error(StatusCodes.Status409Conflict, exception.Code, exception.Message, context);
        }

        var outcome = await service.ImportCoreAsync(
            new PackageImportRequest(
                request.PackagePath,
                actor,
                caseId,
                operationId,
                request.SelectedStockPath,
                request.MachineId,
                request.RememberMachineMapping,
                request.OverwriteManualWorkCs,
                request.ExpectedCamSetupVersion),
            checkActiveImport: true,
            expectedActiveImportId: string.IsNullOrWhiteSpace(request.ExpectedCurrentImportId) ? null : request.ExpectedCurrentImportId.Trim(),
            recordBlocked: false,
            cancellationToken);
        var result = outcome.Result;
        var report = SimulationPackageReport.From(result.Validation, result.ImportDiagnostics, result.MachineCandidates, result.MachineId, result.MachineMatchedBy);
        if (outcome.Conflict is { } conflict)
        {
            var changedBy = conflict.ChangedBy;
            if (!string.IsNullOrWhiteSpace(changedBy)
                && await accounts.FindDisplayNameAsync(changedBy, cancellationToken) is { } displayName
                && !string.Equals(displayName, changedBy, StringComparison.OrdinalIgnoreCase))
            {
                changedBy = $"{displayName} ({changedBy})";
            }

            return Results.Json(new
            {
                error = new
                {
                    code = conflict.Code,
                    message = conflict.Message,
                    correlationId = context.TraceIdentifier,
                    details = new[] { new { currentCamSetupVersion = conflict.CurrentCamSetupVersion, currentImportId = conflict.CurrentImportId } },
                    conflict = new
                    {
                        resource = conflict.Code == "cam_setup_changed" ? "Operation CAM setup" : "Cimatron simulation package",
                        changedBy,
                        changedAt = conflict.ChangedAt,
                        advice = "Someone changed this Operation's CAM setup or imported another package after you opened it, so the package was not imported. " +
                                 "Refresh, check what changed, then import again."
                    }
                },
                report
            }, statusCode: StatusCodes.Status409Conflict);
        }

        switch (result.Outcome)
        {
            case PackageImportOutcome.Imported:
                return Results.Json(new { outcome = "imported", import = SimulationPackageImportResponse.From(outcome.Import!), report },
                    statusCode: StatusCodes.Status201Created);
            case PackageImportOutcome.AlreadyImported:
                return Results.Ok(new { outcome = "already_imported", import = outcome.Import is null ? null : SimulationPackageImportResponse.From(outcome.Import), report });
            default:
                var codes = report.Diagnostics.Where(item => item.Severity == SimulationPackage.Diagnostics.DiagnosticSeverity.Error)
                    .Select(item => new { code = item.Code, message = item.Message, context = item.Context }).ToArray();
                return Results.Json(new
                {
                    error = new
                    {
                        code = "simulation_package_blocked",
                        message = "The package was not imported: " + string.Join(" ", codes.Select(item => item.message).Distinct()),
                        correlationId = context.TraceIdentifier,
                        details = codes
                    },
                    report
                }, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }

    private static async Task<IResult> ListMappingsAsync(SqliteSimulationPackageRepository repository, CancellationToken cancellationToken)
    {
        var items = await repository.ListMappingsAsync(cancellationToken);
        return Results.Ok(new { items = items.Select(ToResponse).ToArray() });
    }

    private static async Task<IResult> AddMappingAsync(
        AddMachineMappingRequest request,
        HttpContext context,
        SqliteSimulationPackageRepository repository,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!PlanningHttpSupport.TryAuthorizeEdit(context, Permissions.ManageSetup, out var authority, out var error)) return error!;
        var field = request.MatchField?.Trim().ToLowerInvariant();
        if (field is null || !MachineMappingFields.All.Contains(field))
        {
            return PlanningHttpSupport.Error(StatusCodes.Status422UnprocessableEntity, "validation_failed",
                $"matchField must be one of: {string.Join(", ", MachineMappingFields.All)}.", context);
        }

        if (string.IsNullOrWhiteSpace(request.MatchValue) || request.MatchValue.Trim().Length > 200)
            return PlanningHttpSupport.Error(StatusCodes.Status422UnprocessableEntity, "validation_failed", "matchValue is required (at most 200 characters).", context);
        if (string.IsNullOrWhiteSpace(request.MachineId))
            return PlanningHttpSupport.Error(StatusCodes.Status422UnprocessableEntity, "validation_failed", "machineId is required.", context);
        try
        {
            var created = await repository.AddMappingAsync(field, request.MatchValue, request.MachineId.Trim(),
                SignedInActor.Require(authority!), timeProvider.GetUtcNow(), cancellationToken);
            return created is null
                ? PlanningHttpSupport.Error(StatusCodes.Status404NotFound, "resource_not_found", "The Machine was not found.", context)
                : Results.Json(ToResponse(created), statusCode: StatusCodes.Status201Created);
        }
        catch (Microsoft.Data.Sqlite.SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            return PlanningHttpSupport.Error(StatusCodes.Status409Conflict, "machine_mapping_exists",
                "This Cimatron machine identity is already mapped (an external id maps to one Machine only).", context);
        }
        catch (EditModeMutationException exception)
        {
            return PlanningHttpSupport.Error(StatusCodes.Status409Conflict, exception.Code, exception.Message, context);
        }
    }

    private static async Task<IResult> DeleteMappingAsync(
        string mappingId,
        HttpContext context,
        SqliteSimulationPackageRepository repository,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!PlanningHttpSupport.TryAuthorizeEdit(context, Permissions.ManageSetup, out var authority, out var error)) return error!;
        try
        {
            return await repository.DeleteMappingAsync(mappingId, SignedInActor.Require(authority!), timeProvider.GetUtcNow(), cancellationToken)
                ? Results.NoContent()
                : PlanningHttpSupport.Error(StatusCodes.Status404NotFound, "resource_not_found", "The Machine mapping was not found.", context);
        }
        catch (EditModeMutationException exception)
        {
            return PlanningHttpSupport.Error(StatusCodes.Status409Conflict, exception.Code, exception.Message, context);
        }
    }

    private static SimulationPackageMachineMappingResponse ToResponse(SimulationPackageMachineMapping item) => new(
        item.MappingId, item.MatchField, item.MatchValue, item.MachineId, item.MachineNumber, item.MachineName, item.CreatedAt, item.CreatedBy);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
