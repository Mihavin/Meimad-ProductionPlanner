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
            ["customerDeliveryDate"] = 11,
            ["batchNumber"] = 16
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
            ["picturePath"] = 21,
            ["productionInstruction"] = 14,
            ["batchNumber"] = 16
        };
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> PlanningHeaderAliases =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["customer"] = ["customer", "customer name", "client", "שם לקוח", "לקוח"],
            ["partNumber"] = ["part number", "part no", "part #", "item number", "item no", "item #", "pn", "מקט", "מספר פריט"],
            ["caseReference"] = ["case", "case reference", "case ref", "job", "job number", "work order"],
            ["batchNumber"] = ["batch number", "batch no", "production order", "\u05e4\u05e7\"\u05e2"],
            ["notes"] = ["notes", "note", "comments", "comment", "remarks"],
            ["quantity"] = ["quantity", "qty", "planned quantity", "batch quantity", "כמות"],
            ["materialStatus"] = ["material status", "material", "stock status"],
            ["startDate"] = ["start date", "planned start", "planning start"],
            ["endDate"] = ["end date", "planned end", "planning end"],
            ["plannerDeliveryDate"] = ["planner delivery date", "planner due date", "internal due date"],
            ["customerDeliveryDate"] = ["customer delivery date", "customer due date"]
        };
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> OpenOrderHeaderAliases =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["partNumber"] = ["part number", "part no", "part #", "item number", "item no", "item #", "pn", "מספר פריט", "מקט"],
            ["orderNumber"] = ["order number", "order no", "order #", "customer order", "customer order number", "po number", "po #", "purchase order", "מספר הזמנה"],
            ["orderLine"] = ["order line", "line number", "line no", "line #", "מספר שורה"],
            ["customer"] = ["customer", "customer name", "client", "שם לקוח", "לקוח"],
            ["deliveryDate"] = ["work finish date", "delivery date", "due date", "finish date", "required date", "תאריך אספקה"],
            ["revision"] = ["revision", "rev"],
            ["outstandingQuantity"] = ["quantity", "qty", "open quantity", "outstanding quantity", "remaining quantity", "balance", "יתרה לאספקה"],
            ["notes"] = ["notes", "note", "comments", "comment", "remarks"],
            ["drawingNumber"] = ["drawing number", "drawing no", "drawing #"],
            ["caseReference"] = ["case", "case reference", "case ref", "job", "job number", "work order"],
            ["orderedQuantity"] = ["ordered quantity", "order quantity", "original quantity", "\u05db\u05de\u05d5\u05ea \u05d1\u05d4\u05d6\u05de\u05e0\u05d4\u05d4"],
            ["itemName"] = ["item name", "part name", "description", "part description"],
            ["picturePath"] = ["picture", "picture path", "image", "image path", "preview", "preview path"],
            ["productionInstruction"] = ["active", "production instruction", "\u05d4\u05d5\u05e8\u05d0\u05ea \u05d9\u05d9\u05e6\u05d5\u05e8"],
            ["batchNumber"] = ["batch number", "batch no", "production order", "\u05e4\u05e7\"\u05e2"]
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
        => await PreviewAsync(
            workbook,
            fileName,
            approvedPlanningSheet: null,
            approvedOpenOrdersSheet: null,
            mappings: null,
            cancellationToken);

    internal async Task<LegacyImportPreviewResponse> PreviewAsync(
        Stream workbook,
        string fileName,
        string? approvedPlanningSheet,
        string? approvedOpenOrdersSheet,
        IReadOnlyList<LegacyColumnMappingRequest>? mappings,
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
            workbookData,
            candidates,
            token,
            expiresAt,
            mappings,
            mappingsAreAuthoritative: mappings is not null,
            useApprovedSheets: !string.IsNullOrWhiteSpace(approvedPlanningSheet)
                || !string.IsNullOrWhiteSpace(approvedOpenOrdersSheet),
            CleanValue(approvedPlanningSheet),
            CleanValue(approvedOpenOrdersSheet));
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
        var requestSha256 = HashApprovedPayload(request);
        var allowAdditionalCaseOrderReceipt = IsCaseOrderOnlyPass(request);
        var durableReplay = await repository.TryReplayAsync(
            request.WorkbookSha256,
            requestSha256,
            allowAdditionalCaseOrderReceipt,
            editAuthority,
            now,
            cancellationToken);
        if (durableReplay is not null)
        {
            return durableReplay;
        }

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
            mappingsAreAuthoritative: HasCompleteRequiredMappings(request),
            useApprovedSheets: true,
            request.PlanningSheet,
            request.OpenOrdersSheet);
        var issues = ValidateSelections(request, approvedPreview);
        if (issues.Count > 0)
        {
            throw new LegacyImportValidationException(issues);
        }

        return await repository.CommitAsync(
            request,
            approvedPreview,
            requestSha256,
            editAuthority,
            cancellationToken);
    }

    private static bool IsCaseOrderOnlyPass(LegacyImportCommitRequest request)
    {
        var selections = request.OpenOrderSelections ?? [];
        return string.IsNullOrWhiteSpace(request.PlanningSheet)
            && !string.IsNullOrWhiteSpace(request.OpenOrdersSheet)
            && (request.PlanningSelections?.Count ?? 0) == 0
            && (request.MachineMappings?.Count ?? 0) == 0
            && selections.Any(selection => selection.Action is "create_case" or "create_order")
            && selections.All(selection => selection.Action is "create_case" or "create_order" or "skip")
            && (request.ColumnMappings ?? []).All(mapping =>
                string.Equals(mapping.Scope, "open_orders", StringComparison.Ordinal));
    }

    private static bool HasCompleteRequiredMappings(LegacyImportCommitRequest request)
    {
        if (request.ColumnMappings is null or { Count: 0 })
        {
            return false;
        }

        var mappedFields = request.ColumnMappings
            .Where(mapping => !string.IsNullOrWhiteSpace(mapping.Scope)
                && !string.IsNullOrWhiteSpace(mapping.Field)
                && !string.IsNullOrWhiteSpace(mapping.Column))
            .Select(mapping => $"{mapping.Scope}:{mapping.Field}")
            .ToHashSet(StringComparer.Ordinal);

        return (string.IsNullOrWhiteSpace(request.PlanningSheet)
                || mappedFields.Contains("planning:partNumber")
                    && mappedFields.Contains("planning:quantity"))
            && (string.IsNullOrWhiteSpace(request.OpenOrdersSheet)
                || mappedFields.Contains("open_orders:partNumber"));
    }

    private static LegacyImportPreviewResponse BuildPreview(
        LegacyWorkbookData workbook,
        LegacyImportCandidatePool candidatePool,
        string token,
        DateTimeOffset expiresAt,
        IReadOnlyList<LegacyColumnMappingRequest>? mappings,
        bool mappingsAreAuthoritative,
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
        var detectedOpenOrderLayout = DetectHeaderLayout(openOrdersSheet, OpenOrderHeaderAliases);
        var orderDrivenBatchSheet = IsOrderDrivenBatchLayout(openOrdersSheet, detectedOpenOrderLayout);
        if (!useApprovedSheets && orderDrivenBatchSheet)
        {
            planningSheet = openOrdersSheet;
        }

        if (useApprovedSheets
            && !string.IsNullOrWhiteSpace(approvedPlanningSheet)
            && planningSheet is null)
        {
            issues.Add(new LegacyImportIssue(
                LegacyImportIssueSeverity.Blocking,
                "planning_sheet_not_found",
                $"Approved planning worksheet '{approvedPlanningSheet}' was not found.",
                Scope: "planning"));
        }
        if (useApprovedSheets
            && !string.IsNullOrWhiteSpace(approvedOpenOrdersSheet)
            && openOrdersSheet is null)
        {
            issues.Add(new LegacyImportIssue(
                LegacyImportIssueSeverity.Blocking,
                "open_orders_sheet_not_found",
                $"Approved open-orders worksheet '{approvedOpenOrdersSheet}' was not found.",
                Scope: "open_orders"));
        }
        if (planningSheet is null)
        {
            issues.Add(new LegacyImportIssue(
                openOrdersSheet is null
                    ? LegacyImportIssueSeverity.Blocking
                    : LegacyImportIssueSeverity.Warning,
                "planning_sheet_not_found",
                openOrdersSheet is null
                    ? "No supported planning or open-orders worksheet was detected."
                    : "No planning worksheet with machine sections was selected; this preview can import Orders only.",
                Scope: "planning"));
        }
        if (openOrdersSheet is null)
        {
            issues.Add(new LegacyImportIssue(
                LegacyImportIssueSeverity.Warning,
                "open_orders_sheet_not_found",
                "No open-order lookup worksheet was detected; order enrichment is unavailable.",
                Scope: "open_orders"));
        }

        foreach (var mapping in mappings?.Where(mapping => mapping.Scope is not ("planning" or "open_orders")) ?? [])
        {
            issues.Add(new LegacyImportIssue(
                LegacyImportIssueSeverity.Blocking,
                "invalid_column_mapping_scope",
                $"Column mapping scope '{mapping.Scope}' is invalid.",
                Field: mapping.Field,
                Scope: mapping.Scope));
        }

        var openOrderDetected = detectedOpenOrderLayout;
        var planningDetected = orderDrivenBatchSheet && ReferenceEquals(planningSheet, openOrdersSheet)
            ? ToOrderDrivenPlanningLayout(openOrderDetected)
            : DetectHeaderLayout(planningSheet, PlanningHeaderAliases);
        var planningColumns = ResolveColumns(
            "planning",
            planningSheet,
            DefaultPlanningColumns,
            planningDetected,
            !orderDrivenBatchSheet && IsLegacyPlanningLayout(planningSheet, planningDetected),
            mappingsAreAuthoritative,
            mappings,
            issues);
        var openOrderColumns = ResolveColumns(
            "open_orders",
            openOrdersSheet,
            DefaultOpenOrderColumns,
            openOrderDetected,
            IsLegacyOpenOrderLayout(openOrdersSheet, openOrderDetected),
            mappingsAreAuthoritative,
            mappings,
            issues);
        var planningColumnsValid = planningSheet is null
            || RequireColumns("planning", planningColumns, ["partNumber", "quantity"], issues);
        var openOrderColumnsValid = openOrdersSheet is null
            || RequireColumns("open_orders", openOrderColumns, ["partNumber"], issues);
        var sections = planningSheet is null || !planningColumnsValid || orderDrivenBatchSheet
            ? []
            : FindMachineSections(planningSheet, planningColumns.Columns, candidatePool.Machines, issues);
        var openOrderRows = openOrdersSheet is null || !openOrderColumnsValid
            ? []
            : BuildOpenOrderRows(openOrdersSheet, openOrderColumns, candidatePool, issues);
        var planningRows = planningSheet is null || !planningColumnsValid
            ? []
            : orderDrivenBatchSheet
                ? BuildOrderDrivenBatchRows(
                    planningSheet,
                    planningColumns.Columns,
                    openOrderColumns.Columns,
                    openOrderRows,
                    candidatePool,
                    issues)
                : BuildPlanningRows(planningSheet, sections, planningColumns.Columns, candidatePool, issues);

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
                    sheet.MaximumColumn,
                    BuildSourceColumns(sheet))).ToArray()),
            new LegacyImportSuggestionsResponse(
                planningSheet?.Name,
                openOrdersSheet?.Name,
                BuildSuggestions("planning", planningSheet, planningColumns),
                BuildSuggestions("open_orders", openOrdersSheet, openOrderColumns)),
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

    private static bool IsPlanningSheet(LegacySheetData sheet)
    {
        var detected = DetectHeaderLayout(sheet, PlanningHeaderAliases);
        if (!detected.Columns.ContainsKey("partNumber")
            || !detected.Columns.ContainsKey("quantity"))
        {
            return false;
        }

        if (string.Equals(sheet.Name, "תכנית ייצור", StringComparison.Ordinal))
        {
            return true;
        }

        if (detected.HeaderRow <= 1
            || !sheet.Rows.TryGetValue(detected.HeaderRow - 1, out var priorRow))
        {
            return false;
        }

        var labels = priorRow.Values
            .Select(cell => CleanValue(cell.Value))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
        return labels.Length == 1 && LooksLikeMachineSectionLabel(labels[0]!);
    }

    private static bool LooksLikeMachineSectionLabel(string label)
    {
        var normalized = NormalizeHeader(label);
        return normalized.Contains("machine", StringComparison.Ordinal)
            || normalized.Contains(NormalizeHeader("מכונה"), StringComparison.Ordinal)
            || Regex.IsMatch(normalized, @"^m\d+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    }

    private static bool IsOpenOrdersSheet(LegacySheetData sheet)
    {
        var detected = DetectHeaderLayout(sheet, OpenOrderHeaderAliases);
        return detected.Columns.ContainsKey("partNumber")
            && detected.Columns.ContainsKey("orderNumber");
    }

    private static bool IsOrderDrivenBatchLayout(
        LegacySheetData? sheet,
        DetectedColumnLayout detected) =>
        sheet is not null
        && ((detected.Columns.ContainsKey("partNumber")
             && detected.Columns.ContainsKey("orderNumber")
             && detected.Columns.ContainsKey("outstandingQuantity")
             && detected.Columns.ContainsKey("orderedQuantity")
             && detected.Columns.ContainsKey("batchNumber"))
            // The supplied workbook is a stable legacy export whose Hebrew text is
            // sometimes decoded through an older code page. Its authoritative A/P
            // layout is still recognizable by the existing legacy-sheet rule.
            || IsLegacyOpenOrderLayout(sheet, detected) && sheet.MaximumColumn >= 16);

    private static DetectedColumnLayout ToOrderDrivenPlanningLayout(DetectedColumnLayout source)
    {
        var columns = new Dictionary<string, int>(StringComparer.Ordinal);
        Copy("partNumber", "partNumber");
        Copy("customer", "customer");
        Copy("quantity", "outstandingQuantity");
        Copy("batchNumber", "batchNumber");
        columns.TryAdd("partNumber", 1);
        columns.TryAdd("customer", 4);
        columns.TryAdd("quantity", 8);
        columns.TryAdd("batchNumber", 16);
        return new DetectedColumnLayout(source.HeaderRow, columns);

        void Copy(string target, string origin)
        {
            if (source.Columns.TryGetValue(origin, out var column))
            {
                columns[target] = column;
            }
        }
    }

    private static ResolvedColumnLayout ResolveColumns(
        string scope,
        LegacySheetData? sheet,
        IReadOnlyDictionary<string, int> defaults,
        DetectedColumnLayout detected,
        bool useLegacyDefaults,
        bool mappingsAreAuthoritative,
        IReadOnlyList<LegacyColumnMappingRequest>? mappings,
        List<LegacyImportIssue> issues)
    {
        var result = !mappingsAreAuthoritative && useLegacyDefaults
            ? new Dictionary<string, int>(defaults, StringComparer.Ordinal)
            : new Dictionary<string, int>(StringComparer.Ordinal);
        var confidence = result.Keys.ToDictionary(field => field, _ => 0.65m, StringComparer.Ordinal);
        if (!mappingsAreAuthoritative && !useLegacyDefaults)
        {
            foreach (var detectedColumn in detected.Columns)
            {
                result[detectedColumn.Key] = detectedColumn.Value;
                confidence[detectedColumn.Key] = 0.98m;
            }
        }
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
                    Field: mapping.Field,
                    Scope: scope));
                continue;
            }

            result[mapping.Field] = column;
            confidence[mapping.Field] = 1.0m;
        }

        var headerRow = detected.HeaderRow > 0
            ? detected.HeaderRow
            : InferHeaderRow(sheet, result);
        return new ResolvedColumnLayout(result, headerRow, confidence);
    }

    private static IReadOnlyList<LegacyColumnSuggestionResponse> BuildSuggestions(
        string scope,
        LegacySheetData? sheet,
        ResolvedColumnLayout layout)
    {
        if (sheet is null)
        {
            return [];
        }

        return layout.Columns.Select(mapping => new LegacyColumnSuggestionResponse(
            mapping.Key,
            OpenXmlLegacyWorkbookReader.ToColumnName(mapping.Value),
            layout.HeaderRow > 0 ? sheet.Cell(layout.HeaderRow, mapping.Value)?.Value : null,
            layout.Confidence.GetValueOrDefault(mapping.Key, 0.50m),
            IsRequiredColumn(scope, mapping.Key))).ToArray();
    }

    private static bool IsRequiredColumn(string scope, string field) => scope switch
    {
        "planning" => field is "partNumber" or "quantity",
        "open_orders" => field == "partNumber",
        _ => false
    };

    private static DetectedColumnLayout DetectHeaderLayout(
        LegacySheetData? sheet,
        IReadOnlyDictionary<string, IReadOnlyList<string>> aliases)
    {
        if (sheet is null)
        {
            return new DetectedColumnLayout(0, new Dictionary<string, int>(StringComparer.Ordinal));
        }

        var bestRow = 0;
        var best = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in sheet.Rows.Where(entry => entry.Key <= 100))
        {
            var candidate = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var cell in row.Value)
            {
                var header = NormalizeHeader(cell.Value.Value);
                if (header.Length == 0)
                {
                    continue;
                }

                foreach (var field in aliases)
                {
                    if (!candidate.ContainsKey(field.Key)
                        && field.Value.Any(alias => NormalizeHeader(alias) == header))
                    {
                        candidate[field.Key] = cell.Key;
                        break;
                    }
                }
            }

            if (candidate.Count > best.Count)
            {
                bestRow = row.Key;
                best = candidate;
            }
        }

        return new DetectedColumnLayout(bestRow, best);
    }

    private static IReadOnlyList<LegacySourceColumnResponse> BuildSourceColumns(LegacySheetData sheet)
    {
        const int maximumDescriptors = 256;
        const int maximumTextLength = 200;
        var detectedHeader = new[]
            {
                DetectHeaderLayout(sheet, PlanningHeaderAliases),
                DetectHeaderLayout(sheet, OpenOrderHeaderAliases)
            }
            .OrderByDescending(layout => layout.Columns.Count)
            .ThenBy(layout => layout.HeaderRow)
            .First();
        var headerRow = detectedHeader.Columns.Count > 0
            ? detectedHeader.HeaderRow
            : sheet.Rows.Where(entry => entry.Key <= 100)
                .Select(entry => new
                {
                    entry.Key,
                    Score = entry.Value.Values.Count(cell => !string.IsNullOrWhiteSpace(CleanValue(cell.Value)))
                })
                .OrderByDescending(entry => entry.Score)
                .ThenBy(entry => entry.Key)
                .FirstOrDefault()?.Key ?? 0;
        var result = new List<LegacySourceColumnResponse>();
        for (var column = 1; column <= Math.Min(sheet.MaximumColumn, maximumDescriptors); column++)
        {
            var header = headerRow == 0 ? null : Truncate(CleanValue(sheet.Cell(headerRow, column)?.Value), maximumTextLength);
            var sample = sheet.Rows
                .Where(entry => entry.Key > headerRow)
                .Select(entry => Truncate(CleanValue(entry.Value.GetValueOrDefault(column)?.Value), maximumTextLength))
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            if (header is null && sample is null)
            {
                continue;
            }
            result.Add(new LegacySourceColumnResponse(
                OpenXmlLegacyWorkbookReader.ToColumnName(column),
                header,
                sample));
        }
        return result;
    }

    private static string? Truncate(string? value, int maximumLength) =>
        value is not null && value.Length > maximumLength ? value[..maximumLength] : value;

    private static bool IsLegacyPlanningLayout(
        LegacySheetData? sheet,
        DetectedColumnLayout detected) =>
        sheet is not null
        && (string.Equals(sheet.Name, "תכנית ייצור", StringComparison.Ordinal)
            || (detected.Columns.TryGetValue("partNumber", out var partColumn)
                && IsPlanningColumnHeader(sheet.Cell(detected.HeaderRow, partColumn)?.Value)
                && detected.Columns.TryGetValue("quantity", out var quantityColumn)
                && IsPlanningQuantityHeader(sheet.Cell(detected.HeaderRow, quantityColumn)?.Value)));

    private static bool IsLegacyOpenOrderLayout(
        LegacySheetData? sheet,
        DetectedColumnLayout detected) =>
        sheet is not null
        && (string.Equals(sheet.Name, "גיליון1", StringComparison.Ordinal)
            || (detected.Columns.TryGetValue("partNumber", out var partColumn)
                && NormalizeHeader(sheet.Cell(detected.HeaderRow, partColumn)?.Value)
                    == NormalizeHeader("מספר פריט")));

    private static int InferHeaderRow(
        LegacySheetData? sheet,
        IReadOnlyDictionary<string, int> columns)
    {
        if (sheet is null || columns.Count == 0)
        {
            return 0;
        }

        return sheet.Rows.Where(entry => entry.Key <= 100)
            .Select(entry => new
            {
                entry.Key,
                Score = columns.Values.Distinct().Count(column =>
                    !string.IsNullOrWhiteSpace(CleanValue(entry.Value.GetValueOrDefault(column)?.Value)))
            })
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.Key)
            .FirstOrDefault()?.Key ?? 0;
    }

    private static bool RequireColumns(
        string scope,
        ResolvedColumnLayout layout,
        IReadOnlyList<string> fields,
        List<LegacyImportIssue> issues)
    {
        var missing = fields.Where(field => !layout.Columns.ContainsKey(field)).ToArray();
        foreach (var field in missing)
        {
            issues.Add(new LegacyImportIssue(
                LegacyImportIssueSeverity.Blocking,
                "required_column_mapping_missing",
                $"The {scope} field '{field}' requires a source-column mapping.",
                Field: field,
                Scope: scope));
        }
        return missing.Length == 0;
    }

    private static LegacyCellData? MappedCell(
        LegacySheetData sheet,
        int rowNumber,
        IReadOnlyDictionary<string, int> columns,
        string field) => columns.TryGetValue(field, out var column)
        ? sheet.Cell(rowNumber, column)
        : null;

    private static IReadOnlyList<LegacyMachineSectionResponse> FindMachineSections(
        LegacySheetData sheet,
        IReadOnlyDictionary<string, int> columns,
        IReadOnlyList<LegacyImportMachineCandidate> machines,
        List<LegacyImportIssue> issues)
    {
        var explicitHeaderRows = sheet.Rows
            .Where(entry => sheet.Rows.TryGetValue(entry.Key + 1, out var nextRow)
                && IsPlanningColumnHeader(nextRow.GetValueOrDefault(columns["partNumber"])?.Value)
                && IsPlanningQuantityHeader(nextRow.GetValueOrDefault(columns["quantity"])?.Value))
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
                sheet.Name,
                Scope: "planning"));
        }

        return sections;
    }

    private static bool IsPlanningColumnHeader(string? value) =>
        HeaderMatches(value, PlanningHeaderAliases["partNumber"]);

    private static bool IsPlanningQuantityHeader(string? value) =>
        HeaderMatches(value, PlanningHeaderAliases["quantity"]);

    private static bool HeaderMatches(string? value, IReadOnlyList<string> aliases)
    {
        var normalized = NormalizeHeader(value);
        return normalized.Length > 0 && aliases.Any(alias => NormalizeHeader(alias) == normalized);
    }

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
                candidate.Machine.Capabilities,
                candidate.Machine.MachineTypeCapabilities,
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

                if (IsPlanningColumnHeader(partCell?.Value))
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
                        LegacyImportIssueSeverity.Blocking,
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
                    CleanValue(MappedCell(sheet, rowNumber, columns, "customer")?.Value),
                    partNumber,
                    CleanValue(MappedCell(sheet, rowNumber, columns, "caseReference")?.Value),
                    CleanValue(MappedCell(sheet, rowNumber, columns, "notes")?.Value),
                    quantity,
                    CleanValue(MappedCell(sheet, rowNumber, columns, "materialStatus")?.Value),
                    ParseDate(MappedCell(sheet, rowNumber, columns, "startDate"), sheet.Name, rowNumber, "startDate", issues),
                    ParseDate(MappedCell(sheet, rowNumber, columns, "endDate"), sheet.Name, rowNumber, "endDate", issues),
                    ParseDate(MappedCell(sheet, rowNumber, columns, "plannerDeliveryDate"), sheet.Name, rowNumber, "plannerDeliveryDate", issues),
                    ParseDate(MappedCell(sheet, rowNumber, columns, "customerDeliveryDate"), sheet.Name, rowNumber, "customerDeliveryDate", issues));
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
        ResolvedColumnLayout layout,
        LegacyImportCandidatePool pool,
        List<LegacyImportIssue> issues)
    {
        var columns = layout.Columns;
        var headerRow = layout.HeaderRow;
        if (headerRow == 0)
        {
            issues.Add(new LegacyImportIssue(
                LegacyImportIssueSeverity.Blocking,
                "open_orders_header_not_found",
                "The open-order worksheet header row was not found.",
                sheet.Name,
                Scope: "open_orders"));
            return [];
        }

        var result = new List<LegacyOpenOrderRowResponse>();
        var sourceOrder = 0;
        var productionInstructionIsActiveFilter = columns.TryGetValue(
                "productionInstruction", out var productionInstructionColumn)
            && (HeaderMatches(sheet.Cell(headerRow, productionInstructionColumn)?.Value,
                    OpenOrderHeaderAliases["productionInstruction"])
                || productionInstructionColumn == 14
                && IsLegacyOpenOrderLayout(sheet, DetectHeaderLayout(sheet, OpenOrderHeaderAliases))
                && !string.IsNullOrWhiteSpace(CleanValue(sheet.Cell(headerRow, 14)?.Value)));
        for (var rowNumber = headerRow + 1; rowNumber <= sheet.MaximumRow; rowNumber++)
        {
            var partNumber = CleanValue(MappedCell(sheet, rowNumber, columns, "partNumber")?.Value);
            var orderNumber = CleanValue(MappedCell(sheet, rowNumber, columns, "orderNumber")?.Value);
            if (string.IsNullOrWhiteSpace(partNumber) && string.IsNullOrWhiteSpace(orderNumber))
            {
                continue;
            }
            if (productionInstructionIsActiveFilter
                && string.IsNullOrWhiteSpace(CleanValue(
                    MappedCell(sheet, rowNumber, columns, "productionInstruction")?.Value)))
            {
                continue;
            }

            sourceOrder++;
            var values = new LegacyOpenOrderValuesResponse(
                partNumber,
                orderNumber,
                CleanValue(MappedCell(sheet, rowNumber, columns, "orderLine")?.Value),
                CleanValue(MappedCell(sheet, rowNumber, columns, "customer")?.Value),
                ParseDate(
                    MappedCell(sheet, rowNumber, columns, "deliveryDate"),
                    sheet.Name,
                    rowNumber,
                    "deliveryDate",
                    issues,
                    LegacyImportIssueSeverity.Blocking),
                CleanValue(MappedCell(sheet, rowNumber, columns, "revision")?.Value),
                ParseNonNegativeInteger(
                    MappedCell(sheet, rowNumber, columns, "outstandingQuantity"),
                    sheet.Name,
                    rowNumber,
                    "outstandingQuantity",
                    issues),
                CleanValue(MappedCell(sheet, rowNumber, columns, "notes")?.Value),
                CleanValue(MappedCell(sheet, rowNumber, columns, "drawingNumber")?.Value),
                CleanValue(MappedCell(sheet, rowNumber, columns, "caseReference")?.Value),
                ParsePositiveInteger(
                    MappedCell(sheet, rowNumber, columns, "orderedQuantity"),
                    sheet.Name,
                    rowNumber,
                    "orderedQuantity",
                    issues),
                CleanValue(MappedCell(sheet, rowNumber, columns, "itemName")?.Value),
                CleanValue(MappedCell(sheet, rowNumber, columns, "picturePath")?.Value),
                CleanValue(MappedCell(sheet, rowNumber, columns, "productionInstruction")?.Value),
                CleanValue(MappedCell(sheet, rowNumber, columns, "batchNumber")?.Value));
            if (string.IsNullOrWhiteSpace(partNumber))
            {
                issues.Add(RowIssue(
                    LegacyImportIssueSeverity.Blocking,
                    "part_number_required",
                    "An open-order row must include a Part Number.",
                    sheet.Name,
                    rowNumber,
                    "partNumber"));
            }
            var hasOrderFacts = !string.IsNullOrWhiteSpace(
                    CleanValue(MappedCell(sheet, rowNumber, columns, "deliveryDate")?.Value))
                || !string.IsNullOrWhiteSpace(
                    CleanValue(MappedCell(sheet, rowNumber, columns, "outstandingQuantity")?.Value))
                || !string.IsNullOrWhiteSpace(
                    CleanValue(MappedCell(sheet, rowNumber, columns, "orderedQuantity")?.Value));
            if (string.IsNullOrWhiteSpace(orderNumber) && hasOrderFacts)
            {
                issues.Add(RowIssue(
                    LegacyImportIssueSeverity.Blocking,
                    "order_number_required",
                    "An Order row must include an Order Number.",
                    sheet.Name,
                    rowNumber,
                    "orderNumber"));
            }
            if (!string.IsNullOrWhiteSpace(orderNumber)
                && (columns.ContainsKey("outstandingQuantity") || columns.ContainsKey("orderedQuantity"))
                && values.OutstandingQuantity is null
                && values.OrderedQuantity is null)
            {
                issues.Add(RowIssue(
                    LegacyImportIssueSeverity.Blocking,
                    "quantity_required",
                    "An Order row must include a positive whole-number quantity.",
                    sheet.Name,
                    rowNumber,
                    "quantity"));
            }
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

        return AggregateOpenOrderRows(result, issues);
    }

    private static IReadOnlyList<LegacyOpenOrderRowResponse> AggregateOpenOrderRows(
        IReadOnlyList<LegacyOpenOrderRowResponse> rows,
        List<LegacyImportIssue> issues)
    {
        var result = new List<LegacyOpenOrderRowResponse>();
        foreach (var group in rows.GroupBy(
                     row => $"{NormalizeIdentifier(row.Values.PartNumber)}|{NormalizeIdentifier(row.Values.OrderNumber)}",
                     StringComparer.Ordinal))
        {
            var ordered = group.OrderBy(row => row.SourceOrder).ToArray();
            var first = ordered[0];
            if (ordered.Length == 1 || string.IsNullOrWhiteSpace(first.Values.OrderNumber))
            {
                result.Add(first);
                continue;
            }

            var orderedQuantity = SumPositive(ordered.Select(row => row.Values.OrderedQuantity));
            var outstandingQuantity = SumPositive(ordered.Select(row => row.Values.OutstandingQuantity));
            var deliveryDate = ordered.Select(row => row.Values.DeliveryDate)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .OrderBy(value => value, StringComparer.Ordinal)
                .FirstOrDefault();
            var values = first.Values with
            {
                DeliveryDate = deliveryDate,
                OutstandingQuantity = outstandingQuantity,
                OrderedQuantity = orderedQuantity
            };
            issues.Add(new LegacyImportIssue(
                LegacyImportIssueSeverity.Warning,
                "related_order_rows_aggregated",
                $"{ordered.Length} workbook rows for Part '{values.PartNumber}' and Order '{values.OrderNumber}' were combined into one Order; quantity is their sum and Work Finish Date is the earliest date.",
                first.SheetName,
                first.RowNumber,
                "orderNumber",
                Scope: "open_orders"));
            result.Add(first with { Values = values });
        }

        return result.OrderBy(row => row.SourceOrder).ToArray();
    }

    private static IReadOnlyList<LegacyPlanningRowResponse> BuildOrderDrivenBatchRows(
        LegacySheetData sheet,
        IReadOnlyDictionary<string, int> planningColumns,
        IReadOnlyDictionary<string, int> orderColumns,
        IReadOnlyList<LegacyOpenOrderRowResponse> openOrderRows,
        LegacyImportCandidatePool pool,
        List<LegacyImportIssue> issues)
    {
        var headerRow = InferHeaderRow(sheet, orderColumns);
        var groupedOrders = openOrderRows.ToDictionary(
            row => $"{NormalizeIdentifier(row.Values.PartNumber)}|{NormalizeIdentifier(row.Values.OrderNumber)}",
            StringComparer.Ordinal);
        var sourceRows = new List<OrderDrivenBatchSource>();
        for (var rowNumber = headerRow + 1; rowNumber <= sheet.MaximumRow; rowNumber++)
        {
            var partNumber = CleanValue(MappedCell(sheet, rowNumber, orderColumns, "partNumber")?.Value);
            var orderNumber = CleanValue(MappedCell(sheet, rowNumber, orderColumns, "orderNumber")?.Value);
            if (string.IsNullOrWhiteSpace(partNumber) && string.IsNullOrWhiteSpace(orderNumber)) continue;

            var productionInstruction = CleanValue(MappedCell(sheet, rowNumber, orderColumns, "productionInstruction")?.Value);
            if (string.IsNullOrWhiteSpace(productionInstruction)) continue;

            var batchNumber = CleanValue(MappedCell(sheet, rowNumber, orderColumns, "batchNumber")?.Value);
            var remaining = ParseNonNegativeInteger(
                MappedCell(sheet, rowNumber, orderColumns, "outstandingQuantity"),
                sheet.Name,
                rowNumber,
                "outstandingQuantity",
                issues);
            if (string.IsNullOrWhiteSpace(batchNumber))
            {
                issues.Add(RowIssue(
                    LegacyImportIssueSeverity.Blocking,
                    "batch_number_required",
                    "An active production row must include a Batch Number (פק\"ע).",
                    sheet.Name,
                    rowNumber,
                    "batchNumber"));
                continue;
            }
            if (string.IsNullOrWhiteSpace(partNumber) || string.IsNullOrWhiteSpace(orderNumber) || remaining is not > 0)
            {
                continue;
            }
            sourceRows.Add(new OrderDrivenBatchSource(
                rowNumber,
                partNumber,
                orderNumber,
                batchNumber,
                remaining.Value,
                CleanValue(MappedCell(sheet, rowNumber, orderColumns, "customer")?.Value)));
        }

        var result = new List<LegacyPlanningRowResponse>();
        var sourceOrder = 0;
        foreach (var group in sourceRows.GroupBy(
                     row => $"{NormalizeIdentifier(row.PartNumber)}|{NormalizeIdentifier(row.BatchNumber)}",
                     StringComparer.Ordinal))
        {
            sourceOrder++;
            var ordered = group.OrderBy(row => row.RowNumber).ToArray();
            var first = ordered[0];
            var total = SumPositive(ordered.Select(row => (int?)row.RemainingQuantity));
            if (total is not > 0)
            {
                issues.Add(RowIssue(
                    LegacyImportIssueSeverity.Blocking,
                    "batch_quantity_invalid",
                    $"The summed remaining quantity for Batch '{first.BatchNumber}' is invalid or too large.",
                    sheet.Name,
                    first.RowNumber,
                    "quantity"));
                continue;
            }

            var relatedOrders = ordered.GroupBy(row => NormalizeIdentifier(row.OrderNumber), StringComparer.Ordinal)
                .Select(orderGroup =>
                {
                    var order = orderGroup.First();
                    var key = $"{NormalizeIdentifier(order.PartNumber)}|{NormalizeIdentifier(order.OrderNumber)}";
                    groupedOrders.TryGetValue(key, out var sourceOrderRow);
                    return new LegacyRelatedOrderResponse(
                        sourceOrderRow?.RowKey ?? $"{sheet.Name}!{order.RowNumber}",
                        order.OrderNumber,
                        SumPositive(orderGroup.Select(row => (int?)row.RemainingQuantity)) ?? 0,
                        sourceOrderRow?.Candidates.Orders.Count == 1
                            ? sourceOrderRow.Candidates.Orders[0].OrderId
                            : null);
                })
                .Where(order => order.Quantity > 0)
                .ToArray();
            var values = new LegacyPlanningValuesResponse(
                first.Customer,
                first.PartNumber,
                first.BatchNumber,
                null,
                total,
                "active production instruction",
                null,
                null,
                null,
                null);
            var candidates = MatchPlanningCandidates(values, pool, issues, sheet.Name, first.RowNumber);
            result.Add(new LegacyPlanningRowResponse(
                $"{sheet.Name}!batch:{first.RowNumber}",
                sheet.Name,
                first.RowNumber,
                $"pool:{NormalizeIdentifier(first.PartNumber)}",
                sourceOrder,
                values,
                BuildProvenance(sheet, first.RowNumber, planningColumns),
                candidates,
                relatedOrders));
        }

        return result;
    }

    private static int? SumPositive(IEnumerable<int?> values)
    {
        long total = 0;
        var found = false;
        foreach (var value in values)
        {
            if (value is not > 0) continue;
            found = true;
            total += value.Value;
            if (total > int.MaxValue) return null;
        }
        return found ? (int)total : null;
    }

    private sealed record OrderDrivenBatchSource(
        int RowNumber,
        string PartNumber,
        string OrderNumber,
        string BatchNumber,
        int RemainingQuantity,
        string? Customer);

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
                operation.BatchNumber,
                operation.CaseId,
                operation.PartNumber,
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

    private static int? ParseNonNegativeInteger(
        LegacyCellData? cell,
        string sheetName,
        int rowNumber,
        string field,
        List<LegacyImportIssue> issues)
    {
        var value = CleanValue(cell?.Value);
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
            && number >= 0
            && number == decimal.Truncate(number)
            && number <= int.MaxValue)
        {
            return (int)number;
        }
        issues.Add(RowIssue(
            LegacyImportIssueSeverity.Blocking,
            "invalid_quantity",
            $"Source value '{value}' is not a non-negative whole-number quantity.",
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
        List<LegacyImportIssue> issues,
        LegacyImportIssueSeverity severity = LegacyImportIssueSeverity.Warning)
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
            severity,
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
        if (string.IsNullOrWhiteSpace(request.PlanningSheet)
            && string.IsNullOrWhiteSpace(request.OpenOrdersSheet))
        {
            issues.Add(FieldIssue(
                "import_sheet_required",
                "Provide at least one approved planningSheet or openOrdersSheet.",
                "sheets"));
        }
        if (request.ColumnMappings is null)
        {
            issues.Add(FieldIssue("column_mappings_required", "columnMappings is required and may be empty only after explicit approval.", "columnMappings"));
        }
        if (request.MachineMappings is null || request.OpenOrderSelections is null || request.PlanningSelections is null)
        {
            issues.Add(FieldIssue("selections_required", "machineMappings, openOrderSelections, and planningSelections are required.", "selections"));
        }
        else if (!request.OpenOrderSelections.Any(selection => selection.Action is not null and not "skip")
                 && !request.PlanningSelections.Any(selection => selection.Action is not null and not "skip"))
        {
            issues.Add(FieldIssue(
                "no_import_actions_selected",
                "Select at least one create or assignment action before committing the import.",
                "selections"));
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
                if (selection.Action == "skip" && !string.IsNullOrWhiteSpace(selection.RowKey))
                {
                    continue;
                }
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
                    var hasExistingCase = !string.IsNullOrWhiteSpace(selection.ExistingCaseId);
                    var hasSourceCase = !string.IsNullOrWhiteSpace(selection.CaseSourceRowKey);
                    if (hasExistingCase == hasSourceCase)
                    {
                        issues.Add(SourceIssue(
                            "exclusive_case_reference_required",
                            "create_order requires exactly one of existingCaseId or caseSourceRowKey.",
                            sourceRow,
                            "existingCaseId"));
                    }
                    else if (hasSourceCase
                             && (!openRows.ContainsKey(selection.CaseSourceRowKey!)
                                 || !request.OpenOrderSelections!.Any(candidate =>
                                     string.Equals(candidate.RowKey, selection.CaseSourceRowKey, StringComparison.Ordinal)
                                     && string.Equals(candidate.Action, "create_case", StringComparison.Ordinal))))
                    {
                        issues.Add(SourceIssue(
                            "case_source_case_required",
                            "caseSourceRowKey must identify an included create_case source row.",
                            sourceRow,
                            "caseSourceRowKey"));
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
            .GroupBy(mapping => mapping.SectionKey!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().MachineId!, StringComparer.Ordinal);
        foreach (var selection in request.PlanningSelections!)
        {
            if (selection.RowKey is null || !planningRows.TryGetValue(selection.RowKey, out var sourceRow))
            {
                if (selection.Action == "skip" && !string.IsNullOrWhiteSpace(selection.RowKey))
                {
                    continue;
                }
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
            switch (selection.Action)
            {
                case "assign_existing_operation":
                    if (string.IsNullOrWhiteSpace(selection.BatchOperationId))
                    {
                        issues.Add(SourceIssue("batch_operation_required", "assign_existing_operation requires batchOperationId.", sourceRow, "batchOperationId"));
                    }
                    ValidateAssignmentTarget(selection, sourceRow, machineMap, issues);
                    break;
                case "create_batch_and_assign":
                    ValidateNewBatchSelection(selection, sourceRow, requireSelectedOperation: true, issues);
                    ValidateAssignmentTarget(selection, sourceRow, machineMap, issues);
                    break;
                case "create_batch_to_pool":
                    ValidateNewBatchSelection(selection, sourceRow, requireSelectedOperation: false, issues);
                    if (!string.IsNullOrWhiteSpace(selection.BatchOperationId)
                        || !string.IsNullOrWhiteSpace(selection.CaseOperationId)
                        || !string.IsNullOrWhiteSpace(selection.MachineId)
                        || selection.CompatibilityOverride is not null)
                    {
                        issues.Add(SourceIssue(
                            "pool_assignment_forbidden",
                            "create_batch_to_pool snapshots the complete Case route and leaves it unassigned; remove selected-operation, Machine, and compatibility-override values.",
                            sourceRow,
                            "machineId"));
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
        var includedScopes = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(request.PlanningSheet)) includedScopes.Add("planning");
        if (!string.IsNullOrWhiteSpace(request.OpenOrdersSheet)) includedScopes.Add("open_orders");
        issues.AddRange(preview.Issues
            .Where(issue => issue.Severity == "blocking"
                && (issue.Scope is not ("planning" or "open_orders") || includedScopes.Contains(issue.Scope))
                && (!issue.RowNumber.HasValue
                    || selectedRows.Contains($"{issue.SheetName}!{issue.RowNumber}")))
            .Select(issue => new LegacyImportIssue(
                LegacyImportIssueSeverity.Blocking,
                issue.Code,
                issue.Message,
                issue.SheetName,
                issue.RowNumber,
                issue.Field,
                issue.SectionKey,
                issue.Scope)));
        return issues;
    }

    private static void ValidateNewBatchSelection(
        LegacyPlanningSelectionRequest selection,
        LegacyPlanningRowResponse sourceRow,
        bool requireSelectedOperation,
        List<LegacyImportIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(selection.CaseId)
            == string.IsNullOrWhiteSpace(selection.CaseSourceRowKey))
        {
            issues.Add(SourceIssue("exclusive_reference_required", "Provide exactly one of caseId or caseSourceRowKey.", sourceRow, "caseId"));
        }
        if (string.IsNullOrWhiteSpace(selection.BatchNumber)
            || (requireSelectedOperation && string.IsNullOrWhiteSpace(selection.CaseOperationId)))
        {
            var message = requireSelectedOperation
                ? "create_batch_and_assign requires a Case reference, caseOperationId, and batchNumber."
                : "create_batch_to_pool requires a Case reference and batchNumber.";
            issues.Add(SourceIssue("batch_mapping_incomplete", message, sourceRow, "batchNumber"));
        }
        if (selection.Allocations is null || selection.Allocations.Count == 0)
        {
            issues.Add(SourceIssue("allocations_required", "A new Batch requires explicit allocations.", sourceRow, "allocations"));
        }
        if (selection.ExpectedCaseRoute is null || selection.ExpectedCaseRoute.Count == 0)
        {
            issues.Add(SourceIssue(
                "case_route_review_required",
                "A new Batch requires the complete reviewed Case route IDs and versions.",
                sourceRow,
                "expectedCaseRoute"));
        }
        else if (selection.ExpectedCaseRoute.Any(operation =>
                     string.IsNullOrWhiteSpace(operation.CaseOperationId)
                     || operation.Version is null or <= 0)
                 || selection.ExpectedCaseRoute
                     .Where(operation => !string.IsNullOrWhiteSpace(operation.CaseOperationId))
                     .GroupBy(operation => operation.CaseOperationId, StringComparer.Ordinal)
                     .Any(group => group.Count() > 1))
        {
            issues.Add(SourceIssue(
                "case_route_review_invalid",
                "expectedCaseRoute must contain each reviewed Case Operation ID exactly once with a positive version.",
                sourceRow,
                "expectedCaseRoute"));
        }
    }

    private static void ValidateAssignmentTarget(
        LegacyPlanningSelectionRequest selection,
        LegacyPlanningRowResponse sourceRow,
        IReadOnlyDictionary<string, string> machineMap,
        List<LegacyImportIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(selection.MachineId)
            && !machineMap.TryGetValue(sourceRow.SectionKey, out _))
        {
            issues.Add(SourceIssue(
                "machine_mapping_required",
                "This action requires an explicit Machine selection or an approved Machine-section mapping.",
                sourceRow,
                "machineId"));
        }
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

    private static string NormalizeHeader(string? value) => string.Concat(
        Normalize(value).ToLowerInvariant().Where(char.IsLetterOrDigit));

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

    private sealed record DetectedColumnLayout(
        int HeaderRow,
        IReadOnlyDictionary<string, int> Columns);

    private sealed record ResolvedColumnLayout(
        IReadOnlyDictionary<string, int> Columns,
        int HeaderRow,
        IReadOnlyDictionary<string, decimal> Confidence);
}
