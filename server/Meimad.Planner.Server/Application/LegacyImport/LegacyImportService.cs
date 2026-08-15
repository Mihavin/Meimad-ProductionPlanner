using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Configuration;
using Meimad.Planner.Server.Domain.LegacyImport;

namespace Meimad.Planner.Server.Application.LegacyImport;

internal sealed partial class LegacyImportService
{
    private static readonly IReadOnlyDictionary<string, int> DefaultPlanningColumns =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["customer"] = 1,
            ["partNumber"] = 2,
            ["caseReference"] = 3,
            ["notes"] = 4,
            ["quantity"] = 6,
            ["materialStatus"] = 7,
            ["startDate"] = 8,
            ["endDate"] = 9,
            ["plannerDeliveryDate"] = 10,
            ["customerDeliveryDate"] = 11
        };
    private static readonly IReadOnlyDictionary<string, int> DefaultOpenOrderColumns =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["partNumber"] = 1,
            ["orderNumber"] = 2,
            ["orderLine"] = 3,
            ["customer"] = 4,
            ["deliveryDate"] = 5,
            ["revision"] = 6,
            ["outstandingQuantity"] = 8,
            ["notes"] = 9,
            ["drawingNumber"] = 10,
            ["caseReference"] = 11,
            ["orderedQuantity"] = 12,
            ["itemName"] = 15,
            ["picturePath"] = 21
        };

    private readonly OpenXmlLegacyWorkbookReader reader;
    private readonly ILegacyImportRepository repository;
    private readonly TimeProvider timeProvider;
    private readonly LegacyImportOptions options;
    private readonly ConcurrentDictionary<string, StagedPreview> staged = new(StringComparer.Ordinal);
    private readonly object stagingGate = new();
    private const int MaximumStagedPreviews = 4;

    public LegacyImportService(
        OpenXmlLegacyWorkbookReader reader,
        ILegacyImportRepository repository,
        TimeProvider timeProvider,
        LegacyImportOptions options)
    {
        this.reader = reader;
        this.repository = repository;
        this.timeProvider = timeProvider;
        this.options = options;
    }

    internal async Task<LegacyImportPreviewResponse> PreviewAsync(
        Stream workbook,
        string fileName,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(Path.GetExtension(fileName), ".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            throw new LegacyWorkbookFormatException(
                "unsupported_workbook_type",
                "Only OpenXML .xlsx workbooks are supported.");
        }

        var workbookData = await reader.ReadAsync(workbook, Path.GetFileName(fileName), cancellationToken);
        var candidates = await repository.ReadCandidatePoolAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var expiresAt = now.Add(options.PreviewLifetime);
        var token = Guid.NewGuid().ToString("N");
        var response = BuildPreview(
            workbookData, candidates, token, expiresAt, mappings: null,
            useApprovedSheets: false, approvedPlanningSheet: null, approvedOpenOrdersSheet: null);
        lock (stagingGate)
        {
            RemoveExpired(now);
            while (staged.Count >= MaximumStagedPreviews)
            {
                var oldest = staged.OrderBy(entry => entry.Value.CreatedAt)
                    .ThenBy(entry => entry.Key, StringComparer.Ordinal)
                    .First();
                staged.TryRemove(oldest.Key, out _);
            }
            staged[token] = new StagedPreview(workbookData, candidates, now, expiresAt);
        }
        return response;
    }

    internal async Task<LegacyImportCommitResponse> CommitAsync(
        LegacyImportCommitRequest request,
        EditAuthority editAuthority,
        CancellationToken cancellationToken)
    {
        var initialIssues = ValidateCommitEnvelope(request);
        if (initialIssues.Count > 0)
        {
            throw new LegacyImportValidationException(initialIssues);
        }
        request = request with { WorkbookSha256 = request.WorkbookSha256!.ToLowerInvariant() };

        var now = timeProvider.GetUtcNow();
        if (!staged.TryGetValue(request.ImportToken!, out var preview) || preview.ExpiresAt <= now)
        {
            staged.TryRemove(request.ImportToken!, out _);
            throw new LegacyImportTokenExpiredException();
        }

        if (!string.Equals(preview.Workbook.Sha256, request.WorkbookSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new LegacyImportValidationException([
                Validation(
                    "workbook_hash_mismatch",
                    "The commit workbookSha256 does not match the staged preview.",
                    "workbookSha256")]);
        }

        var approvedPreview = BuildPreview(
            preview.Workbook,
            preview.Candidates,
            request.ImportToken!,
            preview.ExpiresAt,
            request.ColumnMappings,
            useApprovedSheets: true,
            request.PlanningSheet,
            request.OpenOrdersSheet);
        var issues = ValidateSelections(request, approvedPreview);
        if (issues.Count > 0)
        {
            throw new LegacyImportValidationException(issues);
        }

        var requestSha256 = HashApprovedPayload(request);
        return await repository.CommitAsync(
            request,
            approvedPreview,
            requestSha256,
            editAuthority,
            cancellationToken);
    }

    private static LegacyImportPreviewResponse BuildPreview(
        LegacyWorkbookData workbook,
        LegacyImportCandidatePool candidatePool,
        string token,
        DateTimeOffset expiresAt,
        IReadOnlyList<LegacyColumnMappingRequest>? mappings,
        bool useApprovedSheets,
        string? approvedPlanningSheet,
        string? approvedOpenOrdersSheet)
    {
        var issues = new List<LegacyImportIssue>();
        var planningSheet = useApprovedSheets
            ? workbook.Sheets.FirstOrDefault(sheet => string.Equals(sheet.Name, approvedPlanningSheet, StringComparison.Ordinal))
            : FindSheet(workbook, "תכנית ייצור", IsPlanningSheet);
        var openOrdersSheet = useApprovedSheets
            ? workbook.Sheets.FirstOrDefault(sheet => string.Equals(sheet.Name, approvedOpenOrdersSheet, StringComparison.Ordinal))
            : FindSheet(workbook, "גיליון1", IsOpenOrdersSheet);
        if (planningSheet is null)
        {
            issues.Add(new LegacyImportIssue(
                LegacyImportIssueSeverity.Blocking,
                "planning_sheet_not_found",
                "No planning worksheet with machine sections was detected."));
        }
        if (openOrdersSheet is null)
        {
            issues.Add(new LegacyImportIssue(
                LegacyImportIssueSeverity.Warning,
                "open_orders_sheet_not_found",
                "No open-order lookup worksheet was detected; order enrichment is unavailable."));
        }

        foreach (var mapping in mappings?.Where(mapping => mapping.Scope is not ("planning" or "open_orders")) ?? [])
        {
            issues.Add(new LegacyImportIssue(
                LegacyImportIssueSeverity.Blocking,
                "invalid_column_mapping_scope",
                $"Column mapping scope '{mapping.Scope}' is invalid.",
                Field: mapping.Field));
        }

        var planningColumns = ResolveColumns("planning", DefaultPlanningColumns, mappings, issues);
        var openOrderColumns = ResolveColumns("open_orders", DefaultOpenOrderColumns, mappings, issues);
        var sections = planningSheet is null
            ? []
            : FindMachineSections(planningSheet, planningColumns, candidatePool.Machines, issues);
        var planningRows = planningSheet is null
            ? []
            : BuildPlanningRows(planningSheet, sections, planningColumns, candidatePool, issues);
        var openOrderRows = openOrdersSheet is null
            ? []
            : BuildOpenOrderRows(openOrdersSheet, openOrderColumns, candidatePool, issues);

        return new LegacyImportPreviewResponse(
            1,
            token,
            workbook.Sha256,
            expiresAt,
            new LegacyWorkbookResponse(
                workbook.FileName,
                workbook.Sheets.Select(sheet => new LegacyWorkbookSheetResponse(
                    sheet.Name,
                    sheet.MaximumRow,
                    sheet.MaximumColumn)).ToArray()),
            new LegacyImportSuggestionsResponse(
                planningSheet?.Name,
                openOrdersSheet?.Name,
                BuildSuggestions(planningSheet, planningColumns),
                BuildSuggestions(openOrdersSheet, openOrderColumns)),
            sections,
            planningRows,
            openOrderRows,
            issues.Select(LegacyImportIssueResponse.FromDomain).ToArray());
    }

    private static LegacySheetData? FindSheet(
        LegacyWorkbookData workbook,
        string preferredName,
        Func<LegacySheetData, bool> predicate) =>
        workbook.Sheets.FirstOrDefault(sheet =>
            string.Equals(sheet.Name, preferredName, StringComparison.Ordinal))
        ?? workbook.Sheets.FirstOrDefault(predicate);

    private static bool IsPlanningSheet(LegacySheetData sheet) => sheet.Rows.Any(entry =>
        sheet.Rows.TryGetValue(entry.Key + 1, out var nextRow)
        && IsPlanningColumnHeader(nextRow.GetValueOrDefault(DefaultPlanningColumns["partNumber"])?.Value)
        && Normalize(nextRow.GetValueOrDefault(DefaultPlanningColumns["quantity"])?.Value) == "כמות");

    private static bool IsOpenOrdersSheet(LegacySheetData sheet)
    {
        var firstRows = sheet.Rows.Where(entry => entry.Key <= 10)
            .SelectMany(entry => entry.Value.Values)
            .Select(cell => Normalize(cell.Value))
            .ToHashSet(StringComparer.Ordinal);
        return firstRows.Contains("מספר פריט") && firstRows.Contains("מספר הזמנה");
    }

    private static IReadOnlyDictionary<string, int> ResolveColumns(
        string scope,
        IReadOnlyDictionary<string, int> defaults,
        IReadOnlyList<LegacyColumnMappingRequest>? mappings,
        List<LegacyImportIssue> issues)
    {
        var result = new Dictionary<string, int>(defaults, StringComparer.Ordinal);
        foreach (var mapping in mappings?.Where(mapping => mapping.Scope == scope) ?? [])
        {
            if (string.IsNullOrWhiteSpace(mapping.Field)
                || !defaults.ContainsKey(mapping.Field)
                || !TryParseColumn(mapping.Column, out var column))
            {
                issues.Add(new LegacyImportIssue(
                    LegacyImportIssueSeverity.Blocking,
                    "invalid_column_mapping",
                    $"Column mapping '{scope}:{mapping.Field}' is invalid.",
                    Field: mapping.Field));
                continue;
            }

            result[mapping.Field] = column;
        }

        return result;
    }

    private static IReadOnlyList<LegacyColumnSuggestionResponse> BuildSuggestions(
        LegacySheetData? sheet,
        IReadOnlyDictionary<string, int> columns)
    {
        if (sheet is null)
        {
            return [];
        }

        var headerRow = sheet.Name == "גיליון1" ? 1 : FindFirstPlanningHeaderRow(sheet);
        return columns.Select(mapping => new LegacyColumnSuggestionResponse(
            mapping.Key,
            OpenXmlLegacyWorkbookReader.ToColumnName(mapping.Value),
            sheet.Cell(headerRow, mapping.Value)?.Value,
            1.0m)).ToArray();
    }

    private static int FindFirstPlanningHeaderRow(LegacySheetData sheet) =>
        sheet.Rows.FirstOrDefault(entry => entry.Value.Values.Any(cell =>
            Normalize(cell.Value) == "כמות")).Key;

    private static IReadOnlyList<LegacyMachineSectionResponse> FindMachineSections(
        LegacySheetData sheet,
        IReadOnlyDictionary<string, int> columns,
        IReadOnlyList<LegacyImportMachineCandidate> machines,
        List<LegacyImportIssue> issues)
    {
        var explicitHeaderRows = sheet.Rows
            .Where(entry => sheet.Rows.TryGetValue(entry.Key + 1, out var nextRow)
                && IsPlanningColumnHeader(nextRow.GetValueOrDefault(columns["partNumber"])?.Value)
                && Normalize(nextRow.GetValueOrDefault(columns["quantity"])?.Value) == "כמות")
            .Select(entry => entry.Key)
            .ToHashSet();
        var sectionStyles = explicitHeaderRows
            .Select(row => sheet.Cell(row, columns["quantity"])?.StyleIndex)
            .Where(style => style.HasValue)
            .Select(style => style!.Value)
            .ToHashSet();
        var headers = sheet.Rows
            .Select(entry => new
            {
                Row = entry.Key,
                Label = explicitHeaderRows.Contains(entry.Key)
                    || (sheet.Cell(entry.Key, columns["quantity"]) is { StyleIndex: not null } labelCell
                        && sectionStyles.Contains(labelCell.StyleIndex.Value)
                        && !string.IsNullOrWhiteSpace(labelCell.Value)
                        && entry.Value.Values.Count(cell => !string.IsNullOrWhiteSpace(CleanValue(cell.Value))) == 1
                        && sheet.Rows.TryGetValue(entry.Key + 1, out var nextRow)
                        && IsPlanningDataRow(nextRow, columns))
                    ? CleanValue(sheet.Cell(entry.Key, columns["quantity"])?.Value)
                    : null
            })
            .Where(entry => entry.Label is not null)
            .ToArray();
        var sections = new List<LegacyMachineSectionResponse>();
        for (var index = 0; index < headers.Length; index++)
        {
            var header = headers[index];
            var lastRow = index + 1 < headers.Length ? headers[index + 1].Row - 1 : sheet.MaximumRow;
            var candidates = MatchMachines(header.Label!, machines);
            var sectionKey = $"{sheet.Name}!{header.Row}";
            if (candidates.Count != 1 || candidates[0].Score < 0.95m)
            {
                issues.Add(new LegacyImportIssue(
                    LegacyImportIssueSeverity.Warning,
                    "machine_mapping_required",
                    $"Machine section '{header.Label}' requires an explicit Machine mapping.",
                    sheet.Name,
                    header.Row,
                    SectionKey: sectionKey));
            }

            sections.Add(new LegacyMachineSectionResponse(
                sectionKey,
                sheet.Name,
                header.Row,
                header.Label!,
                Math.Min(header.Row + 1, lastRow),
                lastRow,
                candidates));
        }

        if (sections.Count == 0)
        {
            issues.Add(new LegacyImportIssue(
                LegacyImportIssueSeverity.Blocking,
                "machine_sections_not_found",
                "The planning worksheet does not contain recognizable Machine section rows.",
                sheet.Name));
        }

        return sections;
    }

    private static bool IsPlanningColumnHeader(string? value) => Normalize(value) is "מקט" or "מספר פריט";

    private static bool IsPlanningDataRow(
        IReadOnlyDictionary<int, LegacyCellData> row,
        IReadOnlyDictionary<string, int> columns) =>
        !string.IsNullOrWhiteSpace(row.GetValueOrDefault(columns["partNumber"])?.Value)
        && !string.IsNullOrWhiteSpace(row.GetValueOrDefault(columns["quantity"])?.Value)
        && !IsPlanningColumnHeader(row.GetValueOrDefault(columns["partNumber"])?.Value);

    private static IReadOnlyList<LegacyMachineCandidateResponse> MatchMachines(
        string label,
        IReadOnlyList<LegacyImportMachineCandidate> machines)
    {
        var normalizedLabel = NormalizeIdentifier(label);
        var numberMatch = MachineNumberRegex().Match(normalizedLabel);
        var sourceNumber = numberMatch.Success ? numberMatch.Groups[1].Value : null;
        return machines.Where(machine => machine.IsActive)
            .Select(machine =>
            {
                var normalizedNumber = NormalizeIdentifier(machine.Number);
                var normalizedName = NormalizeIdentifier(machine.Name);
                var machineNumberMatch = MachineNumberRegex().Match(normalizedNumber);
                var exactNumber = sourceNumber is not null
                    && int.TryParse(sourceNumber, NumberStyles.None, CultureInfo.InvariantCulture, out var sourceNumberValue)
                    && ((int.TryParse(normalizedNumber, NumberStyles.None, CultureInfo.InvariantCulture, out var plainMachineNumber)
                            && plainMachineNumber == sourceNumberValue)
                        || (machineNumberMatch.Success
                            && int.TryParse(machineNumberMatch.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var embeddedMachineNumber)
                            && embeddedMachineNumber == sourceNumberValue));
                var nameContained = normalizedLabel.Contains(normalizedName, StringComparison.Ordinal)
                    || normalizedName.Contains(normalizedLabel, StringComparison.Ordinal);
                var score = exactNumber ? 1.0m : nameContained ? 0.80m : 0m;
                var reason = exactNumber ? "machine_number_exact" : nameContained ? "machine_name_overlap" : "manual_choice";
                return new { Machine = machine, Score = score, Reason = reason };
            })
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Machine.Number, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Machine.MachineId, StringComparer.Ordinal)
            .Select(candidate => new LegacyMachineCandidateResponse(
                candidate.Machine.MachineId,
                candidate.Machine.Number,
                candidate.Machine.Name,
                candidate.Machine.ProcessType,
                candidate.Machine.AxisType,
                candidate.Score,
                candidate.Reason))
            .ToArray();
    }

    private static IReadOnlyList<LegacyPlanningRowResponse> BuildPlanningRows(
        LegacySheetData sheet,
        IReadOnlyList<LegacyMachineSectionResponse> sections,
        IReadOnlyDictionary<string, int> columns,
        LegacyImportCandidatePool pool,
        List<LegacyImportIssue> issues)
    {
        var result = new List<LegacyPlanningRowResponse>();
        var fingerprints = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var section in sections)
        {
            var sourceOrder = 0;
            for (var rowNumber = section.FirstDataRow; rowNumber <= section.LastDataRow; rowNumber++)
            {
                var partCell = sheet.Cell(rowNumber, columns["partNumber"]);
                var quantityCell = sheet.Cell(rowNumber, columns["quantity"]);
                if (string.IsNullOrWhiteSpace(partCell?.Value)
                    && string.IsNullOrWhiteSpace(quantityCell?.Value))
                {
                    continue;
                }

                if (Normalize(partCell?.Value) is "מקט" or "מספר פריט")
                {
                    continue;
                }

                sourceOrder++;
                var rowKey = $"{sheet.Name}!{rowNumber}";
                var quantity = ParsePositiveInteger(quantityCell, sheet.Name, rowNumber, "quantity", issues);
                var partNumber = CleanValue(partCell?.Value);
                if (string.IsNullOrWhiteSpace(partNumber))
                {
                    issues.Add(RowIssue(
                        LegacyImportIssueSeverity.Warning,
                        "part_number_required",
                        "A planning row with a quantity has no Part Number.",
                        sheet.Name,
                        rowNumber,
                        "partNumber",
                        section.SectionKey));
                }
                if (string.IsNullOrWhiteSpace(quantityCell?.Value))
                {
                    issues.Add(RowIssue(
                        LegacyImportIssueSeverity.Blocking,
                        "quantity_required",
                        "A planning row with a Part Number has no quantity.",
                        sheet.Name,
                        rowNumber,
                        "quantity",
                        section.SectionKey));
                }

                var values = new LegacyPlanningValuesResponse(
                    CleanValue(sheet.Cell(rowNumber, columns["customer"])?.Value),
                    partNumber,
                    CleanValue(sheet.Cell(rowNumber, columns["caseReference"])?.Value),
                    CleanValue(sheet.Cell(rowNumber, columns["notes"])?.Value),
                    quantity,
                    CleanValue(sheet.Cell(rowNumber, columns["materialStatus"])?.Value),
                    ParseDate(sheet.Cell(rowNumber, columns["startDate"]), sheet.Name, rowNumber, "startDate", issues),
                    ParseDate(sheet.Cell(rowNumber, columns["endDate"]), sheet.Name, rowNumber, "endDate", issues),
                    ParseDate(sheet.Cell(rowNumber, columns["plannerDeliveryDate"]), sheet.Name, rowNumber, "plannerDeliveryDate", issues),
                    ParseDate(sheet.Cell(rowNumber, columns["customerDeliveryDate"]), sheet.Name, rowNumber, "customerDeliveryDate", issues));
                var candidates = MatchPlanningCandidates(values, pool, issues, sheet.Name, rowNumber);
                var provenance = BuildProvenance(sheet, rowNumber, columns);
                AddCellIssues(provenance, sheet.Name, rowNumber, section.SectionKey, issues);
                var fingerprint = string.Join('|', section.SectionKey, NormalizeIdentifier(partNumber),
                    NormalizeIdentifier(values.CaseReference), quantity?.ToString(CultureInfo.InvariantCulture),
                    values.CustomerDeliveryDate);
                if (fingerprints.TryGetValue(fingerprint, out var priorRow))
                {
                    issues.Add(RowIssue(
                        LegacyImportIssueSeverity.Warning,
                        "duplicate_source_row",
                        $"This row duplicates source row '{priorRow}'. Select at most one after correcting the workbook.",
                        sheet.Name,
                        rowNumber,
                        sectionKey: section.SectionKey));
                }
                else
                {
                    fingerprints[fingerprint] = rowKey;
                }

                result.Add(new LegacyPlanningRowResponse(
                    rowKey,
                    sheet.Name,
                    rowNumber,
                    section.SectionKey,
                    sourceOrder,
                    values,
                    provenance,
                    candidates));
            }
        }

        return result;
    }

    private static IReadOnlyList<LegacyOpenOrderRowResponse> BuildOpenOrderRows(
        LegacySheetData sheet,
        IReadOnlyDictionary<string, int> columns,
        LegacyImportCandidatePool pool,
        List<LegacyImportIssue> issues)
    {
        var headerRow = sheet.Rows.FirstOrDefault(entry =>
            Normalize(entry.Value.GetValueOrDefault(columns["partNumber"])?.Value) == "מספר פריט").Key;
        if (headerRow == 0)
        {
            issues.Add(new LegacyImportIssue(
                LegacyImportIssueSeverity.Blocking,
                "open_orders_header_not_found",
                "The open-order worksheet header row was not found.",
                sheet.Name));
            return [];
        }

        var result = new List<LegacyOpenOrderRowResponse>();
        var sourceOrder = 0;
        for (var rowNumber = headerRow + 1; rowNumber <= sheet.MaximumRow; rowNumber++)
        {
            var partNumber = CleanValue(sheet.Cell(rowNumber, columns["partNumber"])?.Value);
            var orderNumber = CleanValue(sheet.Cell(rowNumber, columns["orderNumber"])?.Value);
            if (string.IsNullOrWhiteSpace(partNumber) && string.IsNullOrWhiteSpace(orderNumber))
            {
                continue;
            }

            sourceOrder++;
            var values = new LegacyOpenOrderValuesResponse(
                partNumber,
                orderNumber,
                CleanValue(sheet.Cell(rowNumber, columns["orderLine"])?.Value),
                CleanValue(sheet.Cell(rowNumber, columns["customer"])?.Value),
                ParseDate(sheet.Cell(rowNumber, columns["deliveryDate"]), sheet.Name, rowNumber, "deliveryDate", issues),
                CleanValue(sheet.Cell(rowNumber, columns["revision"])?.Value),
                ParsePositiveInteger(sheet.Cell(rowNumber, columns["outstandingQuantity"]), sheet.Name, rowNumber, "outstandingQuantity", issues, required: false),
                CleanValue(sheet.Cell(rowNumber, columns["notes"])?.Value),
                CleanValue(sheet.Cell(rowNumber, columns["drawingNumber"])?.Value),
                CleanValue(sheet.Cell(rowNumber, columns["caseReference"])?.Value),
                ParsePositiveInteger(sheet.Cell(rowNumber, columns["orderedQuantity"]), sheet.Name, rowNumber, "orderedQuantity", issues, required: false),
                CleanValue(sheet.Cell(rowNumber, columns["itemName"])?.Value),
                CleanValue(sheet.Cell(rowNumber, columns["picturePath"])?.Value));
            var matchingCases = MatchCases(values.PartNumber, values.Revision, values.Customer, pool.Cases);
            var caseIds = matchingCases.Select(candidate => candidate.CaseId).ToHashSet(StringComparer.Ordinal);
            var matchingOrders = pool.Orders.Where(order =>
                    caseIds.Contains(order.CaseId)
                    && string.Equals(
                        NormalizeIdentifier(order.OrderNumber),
                        NormalizeIdentifier(values.OrderNumber),
                        StringComparison.Ordinal))
                .OrderBy(order => order.OrderId, StringComparer.Ordinal)
                .Take(5)
                .Select(order => ToResponse(order, "order_number_and_case_exact"))
                .ToArray();
            if (matchingCases.Count > 1)
            {
                issues.Add(RowIssue(
                    LegacyImportIssueSeverity.Warning,
                    "case_mapping_ambiguous",
                    "Multiple existing Cases match this open-order row; select one explicitly.",
                    sheet.Name,
                    rowNumber,
                    "partNumber"));
            }

            var provenance = BuildProvenance(sheet, rowNumber, columns);
            AddCellIssues(provenance, sheet.Name, rowNumber, null, issues);
            result.Add(new LegacyOpenOrderRowResponse(
                $"{sheet.Name}!{rowNumber}",
                sheet.Name,
                rowNumber,
                sourceOrder,
                values,
                provenance,
                new LegacyOpenOrderCandidatesResponse(matchingCases, matchingOrders)));
        }

        return result;
    }

    private static LegacyPlanningCandidatesResponse MatchPlanningCandidates(
        LegacyPlanningValuesResponse values,
        LegacyImportCandidatePool pool,
        List<LegacyImportIssue> issues,
        string sheetName,
        int rowNumber)
    {
        var cases = MatchCases(values.PartNumber, null, values.Customer, pool.Cases);
        if (cases.Count > 1)
        {
            issues.Add(RowIssue(
                LegacyImportIssueSeverity.Warning,
                "case_mapping_ambiguous",
                "Multiple existing Cases match this planning row; select one explicitly.",
                sheetName,
                rowNumber,
                "partNumber"));
        }

        var caseIds = cases.Select(candidate => candidate.CaseId).ToHashSet(StringComparer.Ordinal);
        var orders = pool.Orders.Where(order => caseIds.Contains(order.CaseId))
            .OrderBy(order => order.WorkFinishDate)
            .ThenBy(order => order.OrderNumber, StringComparer.Ordinal)
            .ThenBy(order => order.OrderId, StringComparer.Ordinal)
            .Take(5)
            .Select(order => ToResponse(order, "case_part_number_exact"))
            .ToArray();
        var batches = pool.Batches.Where(batch => caseIds.Contains(batch.CaseId))
            .OrderBy(batch => batch.BatchNumber, StringComparer.Ordinal)
            .ThenBy(batch => batch.BatchId, StringComparer.Ordinal)
            .Take(5)
            .Select(batch => new LegacyBatchCandidateResponse(
                batch.BatchId,
                batch.BatchNumber,
                batch.PlannedQuantity,
                "case_part_number_exact"))
            .ToArray();
        var batchIds = batches.Select(batch => batch.BatchId).ToHashSet(StringComparer.Ordinal);
        var caseOperations = pool.CaseOperations
            .Where(operation => caseIds.Contains(operation.CaseId))
            .OrderBy(operation => operation.OperationNumber)
            .ThenBy(operation => operation.CaseOperationId, StringComparer.Ordinal)
            .Take(20)
            .Select(operation => new LegacyCaseOperationCandidateResponse(
                operation.CaseOperationId,
                operation.CaseId,
                operation.OperationNumber,
                operation.Name,
                operation.RequiredMachineType,
                operation.SetupTimeSeconds,
                operation.CycleTimePerPartSeconds,
                operation.Version))
            .ToArray();
        var batchOperations = pool.BatchOperations
            .Where(operation => batchIds.Contains(operation.BatchId))
            .OrderBy(operation => operation.OperationNumber)
            .ThenBy(operation => operation.BatchOperationId, StringComparer.Ordinal)
            .Take(40)
            .Select(operation => new LegacyBatchOperationCandidateResponse(
                operation.BatchOperationId,
                operation.BatchId,
                operation.SourceCaseOperationId,
                operation.OperationNumber,
                operation.Name,
                operation.Status,
                operation.RequiredMachineType,
                operation.Version,
                operation.AssignmentId,
                operation.MachineId,
                operation.AssignmentVersion))
            .ToArray();
        return new LegacyPlanningCandidatesResponse(cases, orders, batches, caseOperations, batchOperations);
    }

    private static IReadOnlyList<LegacyCaseCandidateResponse> MatchCases(
        string? partNumber,
        string? revision,
        string? customer,
        IReadOnlyList<LegacyImportCaseCandidate> cases)
    {
        var normalizedPart = NormalizeIdentifier(partNumber);
        if (string.IsNullOrEmpty(normalizedPart))
        {
            return [];
        }

        return cases.Where(candidate => string.Equals(
                NormalizeIdentifier(candidate.PartNumber),
                normalizedPart,
                StringComparison.Ordinal))
            .OrderByDescending(candidate =>
                !string.IsNullOrWhiteSpace(revision)
                && string.Equals(NormalizeIdentifier(candidate.Revision), NormalizeIdentifier(revision), StringComparison.Ordinal))
            .ThenByDescending(candidate =>
                !string.IsNullOrWhiteSpace(customer)
                && string.Equals(NormalizeIdentifier(candidate.Customer), NormalizeIdentifier(customer), StringComparison.Ordinal))
            .ThenBy(candidate => candidate.CaseId, StringComparer.Ordinal)
            .Take(5)
            .Select(candidate => new LegacyCaseCandidateResponse(
                candidate.CaseId,
                candidate.PartNumber,
                candidate.Name,
                candidate.Revision,
                candidate.Customer,
                "part_number_exact"))
            .ToArray();
    }

    private static LegacyOrderCandidateResponse ToResponse(LegacyImportOrderCandidate order, string reason) => new(
        order.OrderId,
        order.OrderNumber,
        order.Quantity,
        order.WorkFinishDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        reason);

    private static IReadOnlyList<LegacyCellProvenanceResponse> BuildProvenance(
        LegacySheetData sheet,
        int rowNumber,
        IReadOnlyDictionary<string, int> columns) => columns
        .Select(mapping => (mapping.Key, Column: mapping.Value, Cell: sheet.Cell(rowNumber, mapping.Value)))
        .Where(item => item.Cell is not null)
        .Select(item => new LegacyCellProvenanceResponse(
            item.Key,
            OpenXmlLegacyWorkbookReader.ToColumnName(item.Column),
            item.Cell!.Address,
            item.Cell.Kind,
            item.Cell.Formula,
            item.Cell.Raw))
        .ToArray();

    private static void AddCellIssues(
        IReadOnlyList<LegacyCellProvenanceResponse> provenance,
        string sheetName,
        int rowNumber,
        string? sectionKey,
        List<LegacyImportIssue> issues)
    {
        foreach (var cell in provenance.Where(cell => cell.Kind is "formula_missing_cache" or "error"))
        {
            var required = cell.Field is "partNumber" or "quantity";
            issues.Add(RowIssue(
                required ? LegacyImportIssueSeverity.Blocking : LegacyImportIssueSeverity.Warning,
                cell.Kind == "error" ? "source_cell_error" : "formula_cache_missing",
                cell.Kind == "error"
                    ? $"Source cell {cell.Address} contains the Excel error '{cell.Raw}'."
                    : $"Formula cell {cell.Address} has no cached value.",
                sheetName,
                rowNumber,
                cell.Field,
                sectionKey));
        }
    }

    private static int? ParsePositiveInteger(
        LegacyCellData? cell,
        string sheetName,
        int rowNumber,
        string field,
        List<LegacyImportIssue> issues,
        bool required = true)
    {
        var value = CleanValue(cell?.Value);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
            && number > 0
            && number == decimal.Truncate(number)
            && number <= int.MaxValue)
        {
            return (int)number;
        }

        issues.Add(RowIssue(
            required ? LegacyImportIssueSeverity.Blocking : LegacyImportIssueSeverity.Warning,
            "invalid_quantity",
            $"Source value '{value}' is not a positive whole-number quantity.",
            sheetName,
            rowNumber,
            field));
        return null;
    }

    private static string? ParseDate(
        LegacyCellData? cell,
        string sheetName,
        int rowNumber,
        string field,
        List<LegacyImportIssue> issues)
    {
        var value = CleanValue(cell?.Value);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial)
            && serial >= 1
            && serial < 2_958_466)
        {
            try
            {
                return DateTime.FromOADate(serial).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            catch (ArgumentException)
            {
                // Fall through to the structured issue below.
            }
        }

        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        issues.Add(RowIssue(
            LegacyImportIssueSeverity.Warning,
            "invalid_date",
            $"Source value '{value}' is not a valid Excel or ISO calendar date.",
            sheetName,
            rowNumber,
            field));
        return null;
    }

    private static IReadOnlyList<LegacyImportIssue> ValidateCommitEnvelope(LegacyImportCommitRequest request)
    {
        var issues = new List<LegacyImportIssue>();
        if (request.SchemaVersion != 1)
        {
            issues.Add(FieldIssue("unsupported_schema_version", "schemaVersion must be 1.", "schemaVersion"));
        }
        if (string.IsNullOrWhiteSpace(request.ImportToken))
        {
            issues.Add(FieldIssue("import_token_required", "importToken is required.", "importToken"));
        }
        if (request.WorkbookSha256 is null
            || request.WorkbookSha256.Length != 64
            || request.WorkbookSha256.Any(character => !Uri.IsHexDigit(character)))
        {
            issues.Add(FieldIssue("workbook_hash_invalid", "workbookSha256 must be a 64-character SHA-256 hex value.", "workbookSha256"));
        }
        if (string.IsNullOrWhiteSpace(request.PlanningSheet))
        {
            issues.Add(FieldIssue("planning_sheet_required", "planningSheet is required.", "planningSheet"));
        }
        if (request.ColumnMappings is null)
        {
            issues.Add(FieldIssue("column_mappings_required", "columnMappings is required and may be empty only after explicit approval.", "columnMappings"));
        }
        if (request.MachineMappings is null || request.OpenOrderSelections is null || request.PlanningSelections is null)
        {
            issues.Add(FieldIssue("selections_required", "machineMappings, openOrderSelections, and planningSelections are required.", "selections"));
        }
        return issues;
    }

    private static IReadOnlyList<LegacyImportIssue> ValidateSelections(
        LegacyImportCommitRequest request,
        LegacyImportPreviewResponse preview)
    {
        var issues = new List<LegacyImportIssue>();
        ValidateUniqueKeys(request.ColumnMappings!, mapping => $"{mapping.Scope}:{mapping.Field}", "columnMappings", issues);
        ValidateUniqueKeys(request.MachineMappings!, mapping => mapping.SectionKey, "machineMappings", issues);
        ValidateUniqueKeys(request.OpenOrderSelections!, selection => selection.RowKey, "openOrderSelections", issues);
        ValidateUniqueKeys(request.PlanningSelections!, selection => selection.RowKey, "planningSelections", issues);
        var openRows = preview.OpenOrderRows.ToDictionary(row => row.RowKey, StringComparer.Ordinal);
        foreach (var selection in request.OpenOrderSelections!)
        {
            if (selection.RowKey is null || !openRows.TryGetValue(selection.RowKey, out var sourceRow))
            {
                issues.Add(FieldIssue("source_row_not_found", $"Open-order row '{selection.RowKey}' is not in the approved preview.", "rowKey"));
                continue;
            }

            switch (selection.Action)
            {
                case "skip":
                    break;
                case "create_case":
                    ValidateNewCase(selection.NewCase, sourceRow, issues);
                    if (selection.Order is not null)
                    {
                        ValidateNewOrder(selection.Order, sourceRow, issues);
                    }
                    break;
                case "create_order":
                    if (string.IsNullOrWhiteSpace(selection.ExistingCaseId))
                    {
                        issues.Add(SourceIssue("existing_case_required", "create_order requires existingCaseId.", sourceRow, "existingCaseId"));
                    }
                    ValidateNewOrder(selection.Order, sourceRow, issues);
                    break;
                default:
                    issues.Add(SourceIssue("invalid_selection_action", $"Open-order action '{selection.Action}' is invalid.", sourceRow, "action"));
                    break;
            }
        }

        var planningRows = preview.Rows.ToDictionary(row => row.RowKey, StringComparer.Ordinal);
        var sectionKeys = preview.MachineSections.Select(section => section.SectionKey).ToHashSet(StringComparer.Ordinal);
        foreach (var mapping in request.MachineMappings!)
        {
            if (mapping.SectionKey is null || !sectionKeys.Contains(mapping.SectionKey) || string.IsNullOrWhiteSpace(mapping.MachineId))
            {
                issues.Add(FieldIssue("invalid_machine_mapping", $"Machine mapping '{mapping.SectionKey}' is incomplete or unknown.", "machineMappings"));
            }
        }
        var machineMap = request.MachineMappings!
            .Where(mapping => mapping.SectionKey is not null && mapping.MachineId is not null)
            .ToDictionary(mapping => mapping.SectionKey!, mapping => mapping.MachineId!, StringComparer.Ordinal);
        foreach (var selection in request.PlanningSelections!)
        {
            if (selection.RowKey is null || !planningRows.TryGetValue(selection.RowKey, out var sourceRow))
            {
                issues.Add(FieldIssue("source_row_not_found", $"Planning row '{selection.RowKey}' is not in the approved preview.", "rowKey"));
                continue;
            }

            if (selection.Action == "skip")
            {
                continue;
            }

            if (sourceRow.Values.Quantity is null || string.IsNullOrWhiteSpace(sourceRow.Values.PartNumber))
            {
                issues.Add(SourceIssue("source_row_invalid", "A selected planning row requires a valid Part Number and quantity.", sourceRow, "rowKey"));
            }
            if (string.IsNullOrWhiteSpace(selection.MachineId)
                && !machineMap.TryGetValue(sourceRow.SectionKey, out _))
            {
                issues.Add(SourceIssue("machine_mapping_required", "A selected planning row requires an explicit Machine mapping.", sourceRow, "machineId"));
            }

            switch (selection.Action)
            {
                case "assign_existing_operation":
                    if (string.IsNullOrWhiteSpace(selection.BatchOperationId))
                    {
                        issues.Add(SourceIssue("batch_operation_required", "assign_existing_operation requires batchOperationId.", sourceRow, "batchOperationId"));
                    }
                    break;
                case "create_batch_and_assign":
                    if (string.IsNullOrWhiteSpace(selection.CaseId)
                        == string.IsNullOrWhiteSpace(selection.CaseSourceRowKey))
                    {
                        issues.Add(SourceIssue("exclusive_reference_required", "Provide exactly one of caseId or caseSourceRowKey.", sourceRow, "caseId"));
                    }
                    if (string.IsNullOrWhiteSpace(selection.CaseOperationId)
                        || string.IsNullOrWhiteSpace(selection.BatchNumber))
                    {
                        issues.Add(SourceIssue("batch_mapping_incomplete", "create_batch_and_assign requires caseId, caseOperationId, and batchNumber.", sourceRow, "batchNumber"));
                    }
                    if (selection.Allocations is null || selection.Allocations.Count == 0)
                    {
                        issues.Add(SourceIssue("allocations_required", "A new Batch requires explicit allocations.", sourceRow, "allocations"));
                    }
                    break;
                default:
                    issues.Add(SourceIssue("invalid_selection_action", $"Planning action '{selection.Action}' is invalid.", sourceRow, "action"));
                    break;
            }
        }

        var selectedRows = request.OpenOrderSelections!.Where(selection => selection.Action != "skip").Select(selection => selection.RowKey)
            .Concat(request.PlanningSelections!.Where(selection => selection.Action != "skip").Select(selection => selection.RowKey))
            .ToHashSet(StringComparer.Ordinal);
        issues.AddRange(preview.Issues
            .Where(issue => issue.Severity == "blocking"
                && (!issue.RowNumber.HasValue
                    || selectedRows.Contains($"{issue.SheetName}!{issue.RowNumber}")))
            .Select(issue => new LegacyImportIssue(
                LegacyImportIssueSeverity.Blocking,
                issue.Code,
                issue.Message,
                issue.SheetName,
                issue.RowNumber,
                issue.Field,
                issue.SectionKey)));
        return issues;
    }

    private static void ValidateNewCase(
        LegacyNewCaseRequest? value,
        LegacyOpenOrderRowResponse source,
        List<LegacyImportIssue> issues)
    {
        if (value is null
            || string.IsNullOrWhiteSpace(value.PartNumber)
            || string.IsNullOrWhiteSpace(value.Name)
            || string.IsNullOrWhiteSpace(value.WorkingFolderPath))
        {
            issues.Add(SourceIssue(
                "new_case_incomplete",
                "create_case requires newCase.partNumber, newCase.name, and newCase.workingFolderPath.",
                source,
                "newCase"));
        }
    }

    private static void ValidateNewOrder(
        LegacyNewOrderRequest? value,
        LegacyOpenOrderRowResponse source,
        List<LegacyImportIssue> issues)
    {
        if (value is null
            || string.IsNullOrWhiteSpace(value.OrderNumber)
            || value.Quantity is null or <= 0
            || !DateOnly.TryParseExact(
                value.WorkFinishDate,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _))
        {
            issues.Add(SourceIssue(
                "new_order_incomplete",
                "A new Order requires orderNumber, a positive quantity, and workFinishDate as yyyy-MM-dd.",
                source,
                "order"));
        }
    }

    private static void ValidateUniqueKeys<T>(
        IReadOnlyList<T> items,
        Func<T, string?> keySelector,
        string field,
        List<LegacyImportIssue> issues)
    {
        var duplicate = items.Select(keySelector)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .GroupBy(key => key, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            issues.Add(FieldIssue("duplicate_selection", $"'{duplicate.Key}' appears more than once in {field}.", field));
        }
    }

    private static string HashApprovedPayload(LegacyImportCommitRequest request)
    {
        var normalized = new
        {
            request.SchemaVersion,
            WorkbookSha256 = request.WorkbookSha256?.ToLowerInvariant(),
            request.PlanningSheet,
            request.OpenOrdersSheet,
            ColumnMappings = request.ColumnMappings,
            MachineMappings = request.MachineMappings,
            OpenOrderSelections = request.OpenOrderSelections,
            PlanningSelections = request.PlanningSelections
        };
        return Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(normalized))));
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        foreach (var entry in staged.Where(entry => entry.Value.ExpiresAt <= now))
        {
            staged.TryRemove(entry.Key, out _);
        }
    }

    private static bool TryParseColumn(string? value, out int column)
    {
        column = 0;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 3)
        {
            return false;
        }

        foreach (var character in value.ToUpperInvariant())
        {
            if (character is < 'A' or > 'Z')
            {
                return false;
            }
            column = (column * 26) + character - 'A' + 1;
        }
        return column is > 0 and <= 16_384;
    }

    private static string? CleanValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return new string(value.Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.Format).ToArray()).Trim();
    }

    private static string Normalize(string? value) => CleanValue(value) ?? string.Empty;

    private static string NormalizeIdentifier(string? value) => string.Concat(
        Normalize(value).Where(character => !char.IsWhiteSpace(character))).ToUpperInvariant();

    private static LegacyImportIssue Validation(string code, string message, string field) =>
        FieldIssue(code, message, field);

    private static LegacyImportIssue FieldIssue(string code, string message, string field) => new(
        LegacyImportIssueSeverity.Blocking,
        code,
        message,
        Field: field);

    private static LegacyImportIssue RowIssue(
        LegacyImportIssueSeverity severity,
        string code,
        string message,
        string sheetName,
        int rowNumber,
        string? field = null,
        string? sectionKey = null) => new(severity, code, message, sheetName, rowNumber, field, sectionKey);

    private static LegacyImportIssue SourceIssue(
        string code,
        string message,
        LegacyOpenOrderRowResponse source,
        string? field = null) => RowIssue(
            LegacyImportIssueSeverity.Blocking,
            code,
            message,
            source.SheetName,
            source.RowNumber,
            field);

    private static LegacyImportIssue SourceIssue(
        string code,
        string message,
        LegacyPlanningRowResponse source,
        string? field = null) => RowIssue(
            LegacyImportIssueSeverity.Blocking,
            code,
            message,
            source.SheetName,
            source.RowNumber,
            field,
            source.SectionKey);

    [GeneratedRegex(@"(?:מכונה|MACHINE)?\s*[-_#]?\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MachineNumberRegex();

    private sealed record StagedPreview(
        LegacyWorkbookData Workbook,
        LegacyImportCandidatePool Candidates,
        DateTimeOffset CreatedAt,
        DateTimeOffset ExpiresAt);
}
