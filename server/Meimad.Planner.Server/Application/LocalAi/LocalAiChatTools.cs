using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Meimad.Planner.Server.Application.ProductionRuns;
using Meimad.Planner.Server.Persistence;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Application.LocalAi;

/// <summary>The Windows client's current selection, sent with a chat question. Every ID is optional.</summary>
internal sealed record ChatContext(string? CaseId = null, string? CaseOperationId = null, string? BatchId = null,
    string? BatchOperationId = null, string? ProcessRevisionId = null, string? GCodeReleaseId = null,
    string? MachineId = null, string? ProductionRunId = null);
/// <summary>What an answer was based on: <c>Kind</c> is CONTEXT, LOOKUP or DOCUMENT.</summary>
internal sealed record ChatSource(string Kind, string Title, string? Reference = null);

/// <summary>
/// The read-only lookups the chat model may request. Each one reads Server-held facts or approved,
/// hash-verified documents and returns compact JSON for the model; none can change anything.
/// </summary>
internal sealed class LocalAiChatTools(SqliteDatabase database, LocalCorpusService corpus, ProductionRunService runs,
    TimeProvider clock, Configuration.TimelineOptions timeline)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    internal static readonly IReadOnlyList<ChatToolDefinition> Definitions =
    [
        Tool("find_cases", "Find Cases (parts) by an approximate or partial part number or part name. Use it when a part number is not found exactly.",
            """{"type":"object","properties":{"query":{"type":"string","description":"Part number or words of the part name"}},"required":["query"]}"""),
        Tool("get_case", "Read one Case (part master): material, raw stock, notes, route operations with setup/cycle times, open orders, recent production batches with operation status, and verified material receipts.",
            """{"type":"object","properties":{"partNumber":{"type":"string","description":"Part number such as 30P450172200-001, or a Case ID"}},"required":["partNumber"]}"""),
        Tool("get_nc_releases", "List released NC programs (G-code) for a Case, newest first: operation, process revision, postprocessor, local revision, program number, release comment, analysis and estimated cycle per Machine.",
            """{"type":"object","properties":{"partNumber":{"type":"string","description":"Part number or Case ID"},"operationNumber":{"type":"integer","description":"Optional operation number such as 20"}},"required":["partNumber"]}"""),
        Tool("material_receipts", "List material receipts verified in the Planner in a date range (factory local dates). Optionally only for one part.",
            """{"type":"object","properties":{"from":{"type":"string","description":"First day, yyyy-MM-dd"},"to":{"type":"string","description":"Last day (inclusive), yyyy-MM-dd"},"partNumber":{"type":"string","description":"Optional part number"}},"required":["from","to"]}"""),
        Tool("material_orders", "Search ERP (Kitaron) material purchase-order lines by material, description, supplier or purchase-order number, or those linked to a part's work orders. Shows ordered/received quantities and delivery dates; ERP lines carry no receipt date.",
            """{"type":"object","properties":{"query":{"type":"string","description":"Material number, alloy/description words, supplier or PO number"},"partNumber":{"type":"string","description":"Optional part number whose work orders use the material"},"openOnly":{"type":"boolean","description":"Only lines not yet closed"}}}"""),
        Tool("search_documents", "Search the approved documents (drawings, parts lists, specifications, notes; PDFs included, scanned pages by OCR) by keywords. Give a part number to search that part's documents. Returns excerpts with document names.",
            """{"type":"object","properties":{"query":{"type":"string","description":"Keywords such as: hole tolerance, surface roughness, material"},"partNumber":{"type":"string","description":"Optional part number to limit the search"}},"required":["query"]}"""),
    ];

    private static ChatToolDefinition Tool(string name, string description, string schema) =>
        new(name, description, JsonDocument.Parse(schema).RootElement.Clone());

    internal TimeZoneInfo Zone { get; } = ResolveZone(timeline.TimeZoneId);
    private static TimeZoneInfo ResolveZone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException) { return TimeZoneInfo.Utc; }
    }

    /// <summary>A short, user-facing description of a lookup shown while the model waits on it.</summary>
    internal static string Describe(ChatToolCall call)
    {
        string Arg(string name) => call.Arguments.ValueKind == JsonValueKind.Object && call.Arguments.TryGetProperty(name, out var value) ? value.ToString() : "";
        return call.Name switch
        {
            "find_cases" => "Finding Cases: " + Arg("query"),
            "get_case" => "Reading Case " + Arg("partNumber"),
            "get_nc_releases" => "Reading NC releases of " + Arg("partNumber"),
            "material_receipts" => $"Reading material receipts {Arg("from")} – {Arg("to")}",
            "material_orders" => "Searching ERP material orders " + Arg("query") + " " + Arg("partNumber"),
            "search_documents" => "Searching documents: " + Arg("query") + " " + Arg("partNumber"),
            _ => "Unknown lookup " + call.Name,
        };
    }

    internal async Task<string> ExecuteAsync(ChatToolCall call, ICollection<ChatSource> sources, CancellationToken token)
    {
        var args = call.Arguments;
        string? Text(string name)
        {
            if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var value)) return null;
            var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False ? value.ToString() : null;
            text = text?.Trim();
            if (text?.Length > 200) throw new ArgumentException($"{name} is too long.");
            return string.IsNullOrEmpty(text) ? null : text;
        }
        try
        {
            switch (call.Name)
            {
                case "find_cases":
                    return Serialize(new { matches = await FindCasesAsync(Text("query") ?? throw new ArgumentException("query is required."), 8, token) });
                case "get_case":
                {
                    var found = await ResolveCaseAsync(Text("partNumber"), token);
                    if (found.Case is null) return found.Message;
                    sources.Add(new("LOOKUP", "Case " + found.Case.PartNumber, found.Case.CaseId));
                    return Serialize(await CaseDetailsAsync(found.Case, token));
                }
                case "get_nc_releases":
                {
                    var found = await ResolveCaseAsync(Text("partNumber"), token);
                    if (found.Case is null) return found.Message;
                    int? operation = int.TryParse(Text("operationNumber"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;
                    sources.Add(new("LOOKUP", "NC releases of " + found.Case.PartNumber + (operation is null ? "" : " op " + operation), found.Case.CaseId));
                    return Serialize(new { partNumber = found.Case.PartNumber, releases = await ReleasesAsync(found.Case.CaseId, operation, null, 20, token) });
                }
                case "material_receipts":
                {
                    var from = Day(Text("from"), "from"); var to = Day(Text("to"), "to");
                    if (to < from || to.DayNumber - from.DayNumber > 366) throw new ArgumentException("Use a range of at most one year with from <= to.");
                    CaseRow? scope = null;
                    if (Text("partNumber") is { } part)
                    {
                        var found = await ResolveCaseAsync(part, token);
                        if (found.Case is null) return found.Message;
                        scope = found.Case;
                    }
                    sources.Add(new("LOOKUP", $"Verified material receipts {from:yyyy-MM-dd} – {to:yyyy-MM-dd}" + (scope is null ? "" : " for " + scope.PartNumber)));
                    return Serialize(await ReceiptsAsync(from, to, scope, token));
                }
                case "material_orders":
                {
                    var query = Text("query"); CaseRow? scope = null;
                    if (Text("partNumber") is { } part)
                    {
                        var found = await ResolveCaseAsync(part, token);
                        if (found.Case is null) return found.Message;
                        scope = found.Case;
                    }
                    if (query is null && scope is null) throw new ArgumentException("Give a query or a part number.");
                    var openOnly = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("openOnly", out var open) && open.ValueKind == JsonValueKind.True;
                    sources.Add(new("LOOKUP", "ERP material orders " + (query ?? scope!.PartNumber)));
                    return Serialize(await MaterialOrdersAsync(query, scope, openOnly, token));
                }
                case "search_documents":
                {
                    var query = Text("query") ?? "";
                    var matches = await corpus.SearchKeywordsAsync(query, Text("partNumber"), 4, token);
                    foreach (var title in matches.Select(match => match.Title).Distinct(StringComparer.OrdinalIgnoreCase))
                        sources.Add(new("DOCUMENT", title, matches.First(match => match.Title == title).DocumentId));
                    return Serialize(new
                    {
                        note = matches.Count == 0 ? "No approved document matched. The document may not be in an approved folder, may be a CAD/3D model, or the index may need a rebuild." : null,
                        results = matches.Select(match => new
                        {
                            document = match.Title,
                            textSource = match.Extraction switch { "PDF_OCR" => "OCR of a scanned PDF (may contain misread characters)", "PDF_MIXED" => "PDF, partly OCR", "PDF_TEXT" => "PDF text", _ => "text file" },
                            excerpt = match.Excerpt,
                        }),
                    });
                }
                default:
                    return "Unknown lookup. Available: " + string.Join(", ", Definitions.Select(tool => tool.Name)) + ".";
            }
        }
        catch (ArgumentException exception) { return "Lookup refused: " + exception.Message; }
        catch (InvalidOperationException exception) { return "Lookup unavailable: " + exception.Message; }
    }

    /// <summary>The current selection described for the system prompt; unknown IDs are reported, never guessed.</summary>
    internal async Task<string> DescribeContextAsync(ChatContext? context, ICollection<ChatSource> sources, CancellationToken token)
    {
        if (context is null) return "";
        var text = new StringBuilder();
        var caseId = context.CaseId;
        var caseOperationId = context.CaseOperationId;
        await using var connection = await database.OpenConnectionAsync(token);
        if (context.BatchOperationId is { } batchOperationId)
        {
            var row = await SingleAsync(connection, """
                SELECT b.case_id AS caseId, bo.source_case_operation_id AS caseOperationId, b.batch_number AS batchNumber, b.status AS batchStatus,
                  b.planned_quantity AS plannedQuantity, bo.operation_number AS operationNumber, bo.name AS operationName, bo.status AS operationStatus,
                  bo.required_machine_type AS machineType, bo.setup_seconds AS setupSeconds, bo.cycle_seconds AS cycleSeconds,
                  bo.actual_start AS actualStart, bo.actual_end AS actualEnd,
                  (SELECT m.number || ' ' || m.name FROM machine_assignments a JOIN machines m ON m.id=a.machine_id WHERE a.batch_operation_id=bo.id LIMIT 1) AS assignedMachine
                FROM batch_operations bo JOIN production_batches b ON b.id=bo.production_batch_id WHERE bo.id=$id
                """, ("$id", batchOperationId), token);
            if (row is null) text.AppendLine("Selected batch operation: no longer exists.");
            else
            {
                caseId ??= row.GetValueOrDefault("caseId") as string; caseOperationId ??= row.GetValueOrDefault("caseOperationId") as string;
                row.Remove("caseId"); row.Remove("caseOperationId");
                text.AppendLine("Selected batch operation (a concrete production launch of one route operation): " + Serialize(row));
                sources.Add(new("CONTEXT", $"Batch {row.GetValueOrDefault("batchNumber")} operation {row.GetValueOrDefault("operationNumber")}", batchOperationId));
            }
        }
        else if (context.BatchId is { } batchId)
        {
            var row = await SingleAsync(connection, "SELECT case_id AS caseId, batch_number AS batchNumber, status, planned_quantity AS plannedQuantity, release_state AS releaseState FROM production_batches WHERE id=$id", ("$id", batchId), token);
            if (row is null) text.AppendLine("Selected production batch: no longer exists.");
            else
            {
                caseId ??= row.GetValueOrDefault("caseId") as string; row.Remove("caseId");
                text.AppendLine("Selected production batch: " + Serialize(row));
                sources.Add(new("CONTEXT", $"Batch {row.GetValueOrDefault("batchNumber")}", batchId));
            }
        }
        if (context.GCodeReleaseId is { } releaseId)
        {
            var row = await SingleAsync(connection, "SELECT co.case_id AS caseId, r.case_operation_id AS caseOperationId FROM gcode_releases r JOIN case_operations co ON co.id=r.case_operation_id WHERE r.id=$id", ("$id", releaseId), token);
            if (row is null) text.AppendLine("Selected NC release: no longer exists.");
            else
            {
                caseId ??= row.GetValueOrDefault("caseId") as string; caseOperationId ??= row.GetValueOrDefault("caseOperationId") as string;
                var release = (await ReleasesAsync((string)row.GetValueOrDefault("caseId")!, null, releaseId, 1, token)).FirstOrDefault();
                text.AppendLine("Selected NC release: " + Serialize(release));
                sources.Add(new("CONTEXT", "Selected NC release", releaseId));
            }
        }
        if (caseOperationId is not null)
        {
            var row = await SingleAsync(connection, """
                SELECT co.case_id AS caseId, co.operation_number AS operationNumber, co.name, co.required_machine_type AS machineType,
                  co.setup_seconds AS setupSeconds, co.cycle_seconds AS cycleSeconds, co.dependency_type AS dependency,
                  (SELECT revision_number FROM process_revisions pr WHERE pr.case_operation_id=co.id AND pr.is_active=1 LIMIT 1) AS activeProcessRevision
                FROM case_operations co WHERE co.id=$id
                """, ("$id", caseOperationId), token);
            if (row is null) text.AppendLine("Selected Case operation: no longer exists.");
            else
            {
                caseId ??= row.GetValueOrDefault("caseId") as string; row.Remove("caseId");
                text.AppendLine("Selected Case operation (route step): " + Serialize(row));
                if (context.GCodeReleaseId is null)
                    text.AppendLine("NC releases of the selected operation (newest first): " + Serialize(await ReleasesForOperationAsync(caseOperationId, token)));
                sources.Add(new("CONTEXT", "Operation " + row.GetValueOrDefault("operationNumber"), caseOperationId));
            }
        }
        if (context.ProcessRevisionId is { } processRevisionId)
        {
            var row = await SingleAsync(connection, "SELECT revision_number AS revision, is_active AS active, change_description AS changeDescription FROM process_revisions WHERE id=$id", ("$id", processRevisionId), token);
            text.AppendLine("Selected process revision: " + (row is null ? "no longer exists." : Serialize(row)));
        }
        if (context.MachineId is { } machineId)
        {
            var row = await SingleAsync(connection, "SELECT number, name, machine_type AS machineType, status, nc_dialect AS ncDialect FROM machines WHERE id=$id", ("$id", machineId), token);
            text.AppendLine("Selected Machine: " + (row is null ? "no longer exists." : Serialize(row)));
            if (row is not null) sources.Add(new("CONTEXT", "Machine " + row.GetValueOrDefault("number"), machineId));
        }
        if (context.ProductionRunId is { } runId)
        {
            try
            {
                var run = await runs.GetAsync(runId, token);
                text.AppendLine("Selected Production Run (physical Machine session): " + Serialize(new
                {
                    run.Status, machineId = run.Assignment?.MachineId,
                    outputs = run.Programs.SelectMany(program => program.Outputs).Select(output => new { output.TargetQuantity, output.ProducedQuantity }),
                }));
                sources.Add(new("CONTEXT", "Production Run", runId));
            }
            catch (ProductionRunNotFoundException) { text.AppendLine("Selected Production Run: no longer exists."); }
        }
        if (caseId is not null)
        {
            var found = await ResolveCaseAsync(caseId, token);
            if (found.Case is null) text.AppendLine("Selected Case: no longer exists.");
            else
            {
                text.Insert(0, "Selected Case: " + Serialize(await CaseDetailsAsync(found.Case, token)) + "\n");
                sources.Add(new("CONTEXT", "Case " + found.Case.PartNumber, found.Case.CaseId));
            }
        }
        return text.ToString();
    }

    internal sealed record CaseRow(string CaseId, string PartNumber, string? Revision, string Name, string? Customer);

    private async Task<(CaseRow? Case, string Message)> ResolveCaseAsync(string? key, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("partNumber is required.");
        key = key.Trim();
        var all = await CasesAsync(token);
        var exact = all.Where(row => row.CaseId == key || string.Equals(row.PartNumber, key, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 0) exact = all.Where(row => Normalize(row.PartNumber) == Normalize(key)).ToList();
        if (exact.Count >= 1)
            // Several Cases can share a part number across revisions; the most recently updated is first in CasesAsync.
            return (exact[0], "");
        var close = await FindCasesAsync(key, 5, token);
        return (null, Serialize(new
        {
            notFound = $"No Case has part number '{key}'.",
            closestMatches = close,
            instruction = "Tell the user the part number was not found exactly and which close match (if any) you used.",
        }));
    }

    private List<CaseRow>? caseCache; private DateTimeOffset caseCacheAt;
    private async Task<List<CaseRow>> CasesAsync(CancellationToken token)
    {
        if (caseCache is { } cached && clock.GetUtcNow() - caseCacheAt < TimeSpan.FromSeconds(30)) return cached;
        var rows = new List<CaseRow>();
        await using var connection = await database.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,part_number,revision,name,customer FROM cases ORDER BY updated_at DESC,id";
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            rows.Add(new(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
        caseCache = rows; caseCacheAt = clock.GetUtcNow();
        return rows;
    }

    internal static string Normalize(string text) => new(text.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());

    internal async Task<IReadOnlyList<object>> FindCasesAsync(string query, int limit, CancellationToken token)
    {
        var wanted = Normalize(query);
        var words = query.ToUpperInvariant().Split((char[])[' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries).Where(word => word.Length > 2).ToArray();
        var scored = new List<(CaseRow Row, int Score)>();
        foreach (var row in await CasesAsync(token))
        {
            var part = Normalize(row.PartNumber);
            var score = 0;
            if (wanted.Length > 0 && part == wanted) score = 1000;
            else if (wanted.Length >= 4 && (part.Contains(wanted, StringComparison.Ordinal) || wanted.Contains(part, StringComparison.Ordinal)))
                score = 500 - Math.Abs(part.Length - wanted.Length);
            else if (wanted.Length >= 4)
            {
                var distance = Distance(part, wanted);
                if (distance <= Math.Max(2, wanted.Length / 4)) score = 300 - 40 * distance;
            }
            if (score == 0 && words.Length > 0 && words.All(word => row.Name.Contains(word, StringComparison.OrdinalIgnoreCase))) score = 200;
            if (score > 0) scored.Add((row, score));
        }
        return scored.OrderByDescending(x => x.Score).ThenBy(x => x.Row.PartNumber, StringComparer.Ordinal).Take(limit)
            .Select(x => (object)new { partNumber = x.Row.PartNumber, revision = x.Row.Revision, name = x.Row.Name, customer = x.Row.Customer, match = x.Score >= 1000 ? "exact" : "approximate" })
            .ToList();
    }

    private static int Distance(string a, string b)
    {
        var previous = new int[b.Length + 1]; var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }

    private async Task<object> CaseDetailsAsync(CaseRow row, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        var master = await SingleAsync(connection, """
            SELECT part_number AS partNumber, revision, name, customer, customer_reference AS customerReference, material,
              material_type AS materialType, material_specification AS materialSpecification, raw_material_form AS rawMaterialForm,
              raw_material_dimensions AS rawMaterialDimensions, raw_stock AS rawStock, substr(notes,1,600) AS notes,
              working_folder_path AS workingFolder
            FROM cases WHERE id=$id
            """, ("$id", row.CaseId), token);
        var operations = await ListAsync(connection, """
            SELECT co.operation_number AS operationNumber, co.name, co.required_machine_type AS machineType,
              round(co.setup_seconds/60.0,1) AS setupMinutes, round(co.cycle_seconds/60.0,2) AS cycleMinutes, co.dependency_type AS dependency,
              (SELECT revision_number FROM process_revisions pr WHERE pr.case_operation_id=co.id AND pr.is_active=1 LIMIT 1) AS activeProcessRevision,
              (SELECT COUNT(*) FROM gcode_releases r WHERE r.case_operation_id=co.id) AS ncReleases
            FROM case_operations co WHERE co.case_id=$id ORDER BY co.route_position, co.operation_number LIMIT 40
            """, ("$id", row.CaseId), token);
        var orders = await ListAsync(connection, """
            SELECT order_reference AS orderReference, quantity, work_finish_date AS dueDate, status FROM orders
            WHERE case_id=$id AND COALESCE(kitaron_history_only,0)=0 AND status NOT IN ('complete','cancelled')
            ORDER BY work_finish_date LIMIT 10
            """, ("$id", row.CaseId), token);
        var batches = await ListAsync(connection, """
            SELECT b.batch_number AS batchNumber, b.status, b.planned_quantity AS plannedQuantity,
              (SELECT group_concat(x, '; ') FROM (SELECT bo.operation_number || ' ' || bo.status ||
                 COALESCE(' on Machine ' || (SELECT m.number FROM machine_assignments a JOIN machines m ON m.id=a.machine_id WHERE a.batch_operation_id=bo.id LIMIT 1), '') AS x
               FROM batch_operations bo WHERE bo.production_batch_id=b.id ORDER BY bo.route_position)) AS operations
            FROM production_batches b WHERE b.case_id=$id ORDER BY b.created_at DESC LIMIT 8
            """, ("$id", row.CaseId), token);
        var receipts = await ListAsync(connection, """
            SELECT received_at AS receivedAt, quantity, unit, external_reference AS reference FROM verified_material_receipts
            WHERE case_id=$id ORDER BY received_at DESC LIMIT 5
            """, ("$id", row.CaseId), token);
        foreach (var receipt in receipts) receipt["receivedAt"] = Local(receipt["receivedAt"] as string);
        return new { @case = master, operations, openOrders = orders, recentBatches = batches, recentVerifiedReceipts = receipts };
    }

    private async Task<List<Dictionary<string, object?>>> ReleasesAsync(string caseId, int? operation, string? releaseId, int limit, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        var rows = await ListAsync(connection, $"""
            SELECT co.operation_number AS operationNumber, co.name AS operationName, pr.revision_number AS processRevision, pr.is_active AS activeProcessRevision,
              p.name AS postprocessor, r.post_specific_revision AS localRevision, r.original_file_name AS fileName,
              (SELECT program_number FROM nc_program_headers h WHERE h.gcode_release_id=r.id) AS programNumber,
              r.released_at AS releasedAt, r.released_by AS releasedBy, r.change_scope AS changeScope, substr(r.release_comment,1,300) AS releaseComment,
              (SELECT a.status || ' (' || COALESCE(a.confidence,'') || ')' FROM gcode_release_analyses a WHERE a.gcode_release_id=r.id ORDER BY a.analyzed_at DESC LIMIT 1) AS analysis,
              (SELECT group_concat('Machine ' || m.number || ': ' || CAST(round(e.estimated_cycle_seconds/60.0,1) AS TEXT) || ' min', '; ')
                 FROM gcode_machine_cycle_estimates e JOIN machines m ON m.id=e.machine_id WHERE e.gcode_release_id=r.id) AS estimatedCycle
            FROM gcode_releases r JOIN case_operations co ON co.id=r.case_operation_id
            LEFT JOIN process_revisions pr ON pr.id=r.process_revision_id LEFT JOIN postprocessors p ON p.id=r.postprocessor_id
            WHERE co.case_id=$case AND ($operation IS NULL OR co.operation_number=$operation) AND ($release IS NULL OR r.id=$release)
            ORDER BY r.released_at DESC LIMIT {Math.Clamp(limit, 1, 50)}
            """, token, ("$case", caseId), ("$operation", operation), ("$release", releaseId));
        foreach (var row in rows) row["releasedAt"] = Local(row["releasedAt"] as string);
        return rows;
    }

    private async Task<List<Dictionary<string, object?>>> ReleasesForOperationAsync(string caseOperationId, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        var row = await SingleAsync(connection, "SELECT case_id, operation_number FROM case_operations WHERE id=$id", ("$id", caseOperationId), token);
        return row is null ? [] : await ReleasesAsync((string)row["case_id"]!, Convert.ToInt32(row["operation_number"], CultureInfo.InvariantCulture), null, 5, token);
    }

    private async Task<object> ReceiptsAsync(DateOnly from, DateOnly to, CaseRow? scope, CancellationToken token)
    {
        var start = Instant(from); var end = Instant(to.AddDays(1));
        await using var connection = await database.OpenConnectionAsync(token);
        var rows = await ListAsync(connection, """
            SELECT r.received_at AS receivedAt, c.part_number AS partNumber, c.name AS partName, r.quantity, r.unit,
              r.external_reference AS reference, substr(r.comment,1,200) AS comment, r.verified_by AS verifiedBy,
              c.material_specification AS materialSpecification, c.raw_material_dimensions AS rawMaterialDimensions
            FROM verified_material_receipts r JOIN cases c ON c.id=r.case_id
            WHERE r.received_at >= $from AND r.received_at < $to AND ($case IS NULL OR r.case_id=$case)
            ORDER BY r.received_at LIMIT 60
            """, token, ("$from", start), ("$to", end), ("$case", scope?.CaseId));
        foreach (var row in rows) row["receivedAt"] = Local(row["receivedAt"] as string);
        return new
        {
            range = $"{from:yyyy-MM-dd} to {to:yyyy-MM-dd} inclusive ({Zone.Id})",
            receipts = rows,
            note = "These are receipts verified in the Planner. ERP stock remains authoritative; ERP purchase-order lines (material_orders) show received quantities without a receipt date.",
        };
    }

    private async Task<object> MaterialOrdersAsync(string? query, CaseRow? scope, bool openOnly, CancellationToken token)
    {
        var terms = (query ?? "").ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(6).ToArray();
        var where = new StringBuilder("active=1");
        if (openOnly) where.Append(" AND closed=0");
        if (scope is not null) where.Append(" AND source_key IN (SELECT w.material_order_source_key FROM work_order_material_orders w JOIN production_batches b ON b.id=w.production_batch_id WHERE b.case_id=$case)");
        for (var i = 0; i < terms.Length; i++)
            where.Append($" AND instr(upper(material_number || ' ' || COALESCE(description,'') || ' ' || COALESCE(supplier,'') || ' ' || purchase_order_number),$t{i})>0");
        var parameters = terms.Select((term, i) => ("$t" + i, (object?)term)).Append(("$case", scope?.CaseId)).ToArray();
        await using var connection = await database.OpenConnectionAsync(token);
        var rows = await ListAsync(connection, $"""
            SELECT purchase_order_number AS purchaseOrder, line_number AS line, material_number AS material, description, supplier,
              ordered_quantity AS ordered, received_quantity AS received, unit, requested_delivery_date AS requestedDelivery,
              approved_delivery_date AS approvedDelivery, status, closed, updated_at AS lastChangedInErpImport
            FROM kitaron_material_orders WHERE {where} ORDER BY updated_at DESC, purchase_order_number DESC LIMIT 25
            """, token, parameters);
        foreach (var row in rows) row["lastChangedInErpImport"] = Local(row["lastChangedInErpImport"] as string);
        return new { lines = rows, note = "ERP purchase-order lines as last imported; lastChangedInErpImport is when the Planner saw a change, not the physical receipt date." };
    }

    private DateOnly Day(string? text, string name) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day
            : throw new ArgumentException($"{name} must be a date yyyy-MM-dd.");

    private string Instant(DateOnly day)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, Zone.GetUtcOffset(local)).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }

    private string? Local(string? instant) =>
        DateTimeOffset.TryParse(instant, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value)
            ? TimeZoneInfo.ConvertTime(value, Zone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : instant;

    private static string Serialize(object? value) => JsonSerializer.Serialize(value, Json);

    private static async Task<Dictionary<string, object?>?> SingleAsync(SqliteConnection connection, string sql, (string Name, object? Value) parameter, CancellationToken token) =>
        (await ListAsync(connection, sql, token, parameter)).FirstOrDefault();

    private static async Task<List<Dictionary<string, object?>>> ListAsync(SqliteConnection connection, string sql, (string Name, object? Value) parameter, CancellationToken token) =>
        await ListAsync(connection, sql, token, parameter);

    private static async Task<List<Dictionary<string, object?>>> ListAsync(SqliteConnection connection, string sql, CancellationToken token, params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        var rows = new List<Dictionary<string, object?>>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < reader.FieldCount; i++)
                if (!reader.IsDBNull(i)) row[reader.GetName(i)] = reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }
}
