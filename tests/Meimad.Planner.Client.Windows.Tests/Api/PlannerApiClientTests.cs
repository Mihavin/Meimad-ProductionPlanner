using System.Net;
using System.Net.Http;
using System.Text;
using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Presentation;

namespace Meimad.Planner.Client.Windows.Tests.Api;

public sealed class PlannerApiClientTests
{
    [Fact]
    public async Task Legacy_working_plan_import_client_uploads_workbook_and_commits_explicit_selections_with_authority()
    {
        const string previewJson = """
            {"schemaVersion":1,"importToken":"import-1","workbookSha256":"abc123","expiresAt":"2026-08-20T10:00:00Z",
             "workbook":{"fileName":"legacy.xlsx","sheets":[{"name":"Planning","rowCount":2,"columnCount":3}]},
             "suggestions":{"planningSheet":"Planning","openOrdersSheet":null,"planningColumns":[],"openOrderColumns":[]},
             "machineSections":[],"rows":[],"openOrderRows":[],"issues":[]}
            """;
        const string receiptJson = """
            {"schemaVersion":1,"workbookSha256":"abc123","commitId":"commit-1","replayed":false,
             "created":{"caseIds":["case-1"],"orderIds":[],"batchIds":["batch-1"],"assignmentIds":["assignment-1"]},
             "unchanged":{"caseIds":[],"orderIds":[],"batchIds":[],"assignmentIds":[]},"machineBacklogs":[]}
            """;
        var handler = new RecordingHandler(Json(HttpStatusCode.OK, previewJson), Json(HttpStatusCode.OK, receiptJson));
        using var api = CreateClient(handler);
        await using var workbook = new MemoryStream([0x50, 0x4B, 0x03, 0x04]);

        var preview = await api.PreviewLegacyWorkingPlanAsync(workbook, "legacy.xlsx");
        var receipt = await api.CommitLegacyWorkingPlanAsync(new LegacyWorkingPlanCommit(
            1, preview.ImportToken, preview.WorkbookSha256, "Planning", null, [], [],
            [new LegacyImportOpenOrderSelection("open-1", "skip", null, null, null)],
            [new LegacyImportPlanningSelection("plan-1", "skip", null, null, null, null, null, [], null)]),
            "windows-1", 42);

        Assert.Equal("import-1", preview.ImportToken);
        Assert.Equal("commit-1", receipt.CommitId);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("/api/v1/imports/legacy-working-plan/preview", handler.Requests[0].Path);
        Assert.Contains("name=workbook", handler.Requests[0].Body, StringComparison.Ordinal);
        Assert.Contains("filename=legacy.xlsx", handler.Requests[0].Body, StringComparison.Ordinal);
        Assert.Equal("/api/v1/imports/legacy-working-plan/commit", handler.Requests[1].Path);
        Assert.Equal("windows-1", handler.Requests[1].ClientId);
        Assert.Equal("42", handler.Requests[1].Generation);
        Assert.Contains("\"importToken\":\"import-1\"", handler.Requests[1].Body, StringComparison.Ordinal);
        Assert.Contains("\"action\":\"skip\"", handler.Requests[1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Legacy_working_plan_import_client_surfaces_server_errors()
    {
        var handler = new RecordingHandler(Json(HttpStatusCode.Gone,
            """{"error":{"code":"import_token_expired","message":"Preview expired","correlationId":"corr-1","details":[]}}"""));
        using var api = CreateClient(handler);
        await using var workbook = new MemoryStream([0x50, 0x4B]);

        var error = await Assert.ThrowsAsync<PlannerApiException>(() =>
            api.PreviewLegacyWorkingPlanAsync(workbook, "legacy.xlsx"));

        Assert.Equal("import_token_expired", error.Code);
    }

    [Fact]
    public async Task Legacy_import_preview_reads_batch_context_and_complete_machine_compatibility_facts()
    {
        const string previewJson = """
            {"schemaVersion":1,"importToken":"import-1","workbookSha256":"abc123","expiresAt":"2026-08-20T10:00:00Z",
             "workbook":{"fileName":"legacy.xlsx","sheets":[{"name":"Planning","rowCount":2,"columnCount":3}]},
             "suggestions":{"planningSheet":"Planning","openOrdersSheet":null,"planningColumns":[],"openOrderColumns":[]},
             "machineSections":[{"sectionKey":"machine-a","sheetName":"Planning","headerRow":1,"sourceLabel":"Five-axis mill","firstDataRow":2,"lastDataRow":2,"candidates":[{"machineId":"machine-1","number":"M1","name":"Mill 1","processType":"milling","axisType":"5-axis","capabilities":["fiveAxis"],"machineTypeCapabilities":["probe"],"score":1,"reason":"Exact"}]}],
             "rows":[{"rowKey":"planning-1","sheetName":"Planning","rowNumber":2,"sectionKey":"machine-a","sourceOrder":1,"values":{"partNumber":"PN-1","quantity":5},"provenance":[],"candidates":{"cases":[],"orders":[],"batches":[{"batchId":"batch-1","batchNumber":"B-104","plannedQuantity":5,"reason":"Exact"}],"caseOperations":[],"batchOperations":[{"batchOperationId":"batch-operation-1","batchId":"batch-1","batchNumber":"B-104","caseId":"case-1","partNumber":"PN-1","caseOperationId":"route-1","operationNumber":10,"name":"Finish milling","status":"not_started","requiredMachineType":"fiveAxis","version":2,"assignmentId":null,"machineId":null,"assignmentVersion":null}]}}],
             "openOrderRows":[],"issues":[]}
            """;
        var handler = new RecordingHandler(Json(HttpStatusCode.OK, previewJson));
        using var api = CreateClient(handler);
        await using var workbook = new MemoryStream([0x50, 0x4B]);

        var preview = await api.PreviewLegacyWorkingPlanAsync(workbook, "legacy.xlsx");

        var machine = Assert.Single(Assert.Single(preview.MachineSections).Candidates);
        Assert.Equal("milling", machine.ProcessType);
        Assert.Equal("5-axis", machine.AxisType);
        Assert.Equal(["fiveAxis"], machine.Capabilities);
        Assert.Equal(["probe"], machine.MachineTypeCapabilities);

        var operation = Assert.Single(preview.Rows.Single().Candidates.BatchOperations!);
        Assert.Equal("B-104", operation.BatchNumber);
        Assert.Equal("case-1", operation.CaseId);
        Assert.Equal("PN-1", operation.PartNumber);
    }

    [Fact]
    public async Task Legacy_import_view_model_requires_editor_and_explicit_resolutions_then_refreshes_on_commit()
    {
        const string previewJson = """
            {"schemaVersion":1,"importToken":"import-1","workbookSha256":"abc123","expiresAt":"2026-08-20T10:00:00Z",
             "workbook":{"fileName":"legacy.xlsx","sheets":[{"name":"Planning","rowCount":2,"columnCount":3}]},
             "suggestions":{"planningSheet":"Planning","openOrdersSheet":"Orders","planningColumns":[{"field":"partNumber","column":"A","header":"Part","confidence":0.9}],"openOrderColumns":[]},
             "machineSections":[{"sectionKey":"machine-a","sheetName":"Planning","headerRow":1,"sourceLabel":"Mill A","firstDataRow":2,"lastDataRow":2,"candidates":[{"machineId":"machine-1","number":"M1","name":"Mill 1","score":1,"reason":"Exact"}]}],
             "rows":[{"rowKey":"planning-1","sheetName":"Planning","rowNumber":2,"sectionKey":"machine-a","sourceOrder":1,"values":{"partNumber":"PN-1"},"provenance":[],"candidates":{"cases":[],"orders":[],"batches":[]}}],
             "openOrderRows":[{"rowKey":"open-1","sheetName":"Orders","rowNumber":2,"sourceOrder":1,"values":{"partNumber":"PN-1"},"provenance":[],"candidates":{"cases":[],"orders":[]}}],"issues":[]}
            """;
        const string receiptJson = """
            {"schemaVersion":1,"workbookSha256":"abc123","commitId":"commit-1","replayed":false,
             "created":{"caseIds":[],"orderIds":[],"batchIds":[],"assignmentIds":[]},
             "unchanged":{"caseIds":[],"orderIds":[],"batchIds":[],"assignmentIds":[]},"machineBacklogs":[]}
            """;
        var handler = new RecordingHandler(Json(HttpStatusCode.OK, previewJson), Json(HttpStatusCode.OK, receiptJson));
        using var api = CreateClient(handler);
        var viewModel = new LegacyExcelImportViewModel(
            _ => new MemoryStream([0x50, 0x4B]), _ => true);
        var refreshed = 0;
        viewModel.ImportCommitted += (_, _) => refreshed++;
        viewModel.AttachSession(api, "windows-1", new EditModeStatus(
            ClientEditState.Editor, 9, null, null, DateTimeOffset.UtcNow, 30));
        viewModel.SetWorkbookSelection("legacy.xlsx");

        await viewModel.PreviewAsync();

        Assert.Equal("Planning", viewModel.SourceSheetName);
        Assert.Equal(1, viewModel.HeaderRowNumber);
        Assert.Single(viewModel.Mappings);
        Assert.Single(viewModel.MachineMappings);
        Assert.Equal(2, viewModel.Rows.Count);
        Assert.All(viewModel.Rows, row => Assert.Equal("Blocked", row.Status));
        Assert.False(viewModel.CanCommit);

        viewModel.MachineMappings.Single().SelectedMachineId = "machine-1";
        foreach (var row in viewModel.Rows) row.IsSkipped = true;
        Assert.False(viewModel.CanCommit);
        Assert.False(viewModel.CommitCommand.CanExecute(null));
        Assert.Equal(0, refreshed);

        viewModel.AttachSession(api, "windows-1", new EditModeStatus(
            ClientEditState.Viewer, 10, null, null, DateTimeOffset.UtcNow, 30));
        Assert.False(viewModel.CanCommit);
        Assert.False(viewModel.CommitCommand.CanExecute(null));
    }

    [Fact]
    public async Task Legacy_import_view_model_keeps_server_validation_errors_visible()
    {
        var handler = new RecordingHandler(Json(HttpStatusCode.UnprocessableEntity,
            """{"error":{"code":"invalid_workbook","message":"The workbook cannot be read","correlationId":"corr-1","details":[]}}"""));
        using var api = CreateClient(handler);
        var viewModel = new LegacyExcelImportViewModel(
            _ => new MemoryStream([0x50, 0x4B]), _ => true);
        viewModel.AttachSession(api, "windows-1", new EditModeStatus(
            ClientEditState.Editor, 9, null, null, DateTimeOffset.UtcNow, 30));
        viewModel.SetWorkbookSelection("legacy.xlsx");

        await viewModel.PreviewAsync();

        Assert.Contains("invalid_workbook", viewModel.ErrorMessage, StringComparison.Ordinal);
        Assert.False(viewModel.CanCommit);
    }

    [Fact]
    public async Task Legacy_import_view_model_allows_a_skipped_blocked_row_and_does_not_require_its_machine_mapping()
    {
        const string previewJson = """
            {"schemaVersion":1,"importToken":"import-1","workbookSha256":"abc123","expiresAt":"2026-08-20T10:00:00Z",
             "workbook":{"fileName":"legacy.xlsx","sheets":[{"name":"Planning","rowCount":3,"columnCount":3}]},
             "suggestions":{"planningSheet":"Planning","openOrdersSheet":null,"planningColumns":[],"openOrderColumns":[]},
             "machineSections":[
               {"sectionKey":"machine-a","sheetName":"Planning","headerRow":1,"sourceLabel":"Mill A","firstDataRow":2,"lastDataRow":2,"candidates":[{"machineId":"machine-1","number":"M1","name":"Mill 1","score":1,"reason":"Exact"}]},
               {"sectionKey":"machine-b","sheetName":"Planning","headerRow":1,"sourceLabel":"Mill B","firstDataRow":3,"lastDataRow":3,"candidates":[{"machineId":"machine-2","number":"M2","name":"Mill 2","score":1,"reason":"Exact"}]}],
             "rows":[
               {"rowKey":"planning-1","sheetName":"Planning","rowNumber":2,"sectionKey":"machine-a","sourceOrder":1,"values":{"partNumber":"PN-1","quantity":1},"provenance":[],"candidates":{"cases":[],"orders":[],"batches":[],"caseOperations":[],"batchOperations":[{"batchOperationId":"operation-1","batchId":"batch-1","caseOperationId":"route-1","operationNumber":1,"name":"Mill","status":"not_started","requiredMachineType":null,"version":1,"assignmentId":null,"machineId":null,"assignmentVersion":null}]}},
               {"rowKey":"planning-2","sheetName":"Planning","rowNumber":3,"sectionKey":"machine-b","sourceOrder":2,"values":{"partNumber":"PN-2","quantity":1},"provenance":[],"candidates":{"cases":[],"orders":[],"batches":[],"caseOperations":[],"batchOperations":[]}}],
             "openOrderRows":[],"issues":[{"severity":"blocking","code":"unmatched_part","message":"No Case match","sheetName":"Planning","rowNumber":3,"field":"partNumber","sectionKey":"machine-b"}]}
            """;
        const string receiptJson = """
            {"schemaVersion":1,"workbookSha256":"abc123","commitId":"commit-1","replayed":false,
             "created":{"caseIds":[],"orderIds":[],"batchIds":[],"assignmentIds":[]},
             "unchanged":{"caseIds":[],"orderIds":[],"batchIds":[],"assignmentIds":[]},"machineBacklogs":[]}
            """;
        var handler = new RecordingHandler(Json(HttpStatusCode.OK, previewJson), Json(HttpStatusCode.OK, receiptJson));
        using var api = CreateClient(handler);
        var viewModel = new LegacyExcelImportViewModel(_ => new MemoryStream([0x50, 0x4B]), _ => true);
        viewModel.AttachSession(api, "windows-1", new EditModeStatus(
            ClientEditState.Editor, 9, null, null, DateTimeOffset.UtcNow, 30));
        viewModel.SetWorkbookSelection("legacy.xlsx");

        await viewModel.PreviewAsync();
        var selected = viewModel.Rows.Single(row => row.RowKey == "planning-1");
        var skipped = viewModel.Rows.Single(row => row.RowKey == "planning-2");
        selected.SelectedExistingOperationCandidate = selected.ExistingOperationCandidates.Single();
        skipped.IsSkipped = true;
        viewModel.MachineMappings.Single(mapping => mapping.SectionKey == "machine-a").SelectedMachineId = "machine-1";

        Assert.Equal("Ready", selected.Status);
        Assert.Equal("Skip", skipped.Status);
        Assert.True(viewModel.CanCommit);
        await viewModel.CommitAsync();
        Assert.Contains("\"sectionKey\":\"machine-a\"", handler.Requests[1].Body, StringComparison.Ordinal);
        Assert.DoesNotContain("machine-b", handler.Requests[1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Legacy_open_order_selection_omits_an_empty_optional_order_and_requires_complete_entered_order_fields()
    {
        var owner = new LegacyExcelImportViewModel();
        var caseCandidate = new LegacyImportCaseCandidate("case-1", "PN-1", "Part", null, null, "Exact");
        var row = LegacyImportRowViewModel.OpenOrder(new LegacyImportOpenOrderRow(
            "open-1", "Orders", 12, 1,
            new LegacyImportOpenOrderValues("PN-1", "SO-source", null, "Acme", null, null, 12,
                null, null, null, null, null, null),
            [], new LegacyImportOpenOrderCandidates([caseCandidate], [])), [], owner);

        row.Decision = "create_case";
        row.NewCasePartNumber = "PN-1";
        row.NewCaseName = "Part";
        row.NewCaseWorkingFolderPath = "C:\\Cases\\PN-1";

        var caseOnly = row.ToOpenOrderSelection();
        Assert.True(row.IsResolved);
        Assert.Null(caseOnly.ExistingCaseId);
        Assert.Null(caseOnly.Order);
        Assert.NotNull(caseOnly.NewCase);
        Assert.Equal("PN-1", row.SourcePartNumber);
        Assert.Equal(12, row.SourceQuantity);
        Assert.Equal("Acme", row.SourceCustomer);
        Assert.Equal("SO-source", row.SourceReferenceOrOrderNumber);
        Assert.Contains("PN-1", row.SourceSummary, StringComparison.Ordinal);

        row.OrderNotes = "Add this order too";
        Assert.False(row.IsResolved);
        Assert.NotNull(row.ToOpenOrderSelection().Order);
        row.OrderNumber = "SO-1";
        row.OrderQuantity = "0";
        row.OrderWorkFinishDate = "2026-13-99";
        Assert.False(row.HasCompleteOrderInput);
        row.OrderQuantity = "12";
        row.OrderWorkFinishDate = "2026-09-01";

        var withOrder = row.ToOpenOrderSelection();
        Assert.True(row.IsResolved);
        Assert.Equal(12, withOrder.Order!.Quantity);
        Assert.IsType<int>(withOrder.Order.Quantity!.Value);
        Assert.Equal("2026-09-01", withOrder.Order.WorkFinishDate);
    }

    [Fact]
    public async Task Legacy_batch_allocations_require_valid_distinct_semantics_and_the_source_quantity_total()
    {
        var owner = new LegacyExcelImportViewModel();
        var caseCandidate = new LegacyImportCaseCandidate("case-1", "PN-1", "Part", null, null, "Exact");
        var routeCandidate = new LegacyImportCaseOperationCandidate(
            "route-1", "case-1", 10, "Mill", "mill", null, null, 1);
        var orderCandidate = new LegacyImportOrderCandidate("order-1", "SO-1", 10, "2026-09-01", "Exact");
        var row = LegacyImportRowViewModel.Planning(new LegacyImportPlanningRow(
            "plan-1", "Planning", 5, "machine-a", 1,
            new LegacyImportPlanningValues("Acme", "PN-1", "Ref-1", null, 10, null, null, null, null, null),
            [], new LegacyImportPlanningCandidates([caseCandidate], [orderCandidate], [], [routeCandidate], [])), [], owner);

        row.Decision = "create_batch_and_assign";
        row.SelectedCaseCandidate = caseCandidate;
        row.SelectedRouteOperationCandidate = routeCandidate;
        row.BatchNumber = "B-1";
        await row.AddAllocationAsync();
        var allocation = Assert.Single(row.Allocations);
        allocation.SelectedOrderCandidate = orderCandidate;
        allocation.Quantity = "10";

        Assert.True(row.IsResolved);
        Assert.Equal(10, row.PlannedQuantity);
        Assert.Equal("PN-1", row.PartNumber);
        Assert.Contains("requires mill", routeCandidate.DisplayName, StringComparison.Ordinal);

        allocation.Quantity = "9";
        Assert.False(row.IsResolved);
        allocation.Quantity = "10";
        allocation.OrderSourceRowKey = "open-1";
        Assert.False(row.IsResolved);
        allocation.OrderSourceRowKey = string.Empty;
        Assert.True(row.IsResolved);

        allocation.Type = "stock";
        allocation.SelectedOrderCandidate = null;
        allocation.Quantity = "5";
        await row.AddAllocationAsync();
        var duplicateStock = row.Allocations.Last();
        duplicateStock.Type = "stock";
        duplicateStock.Quantity = "5";
        Assert.False(row.IsResolved);
        await row.RemoveAllocationAsync(duplicateStock);
        allocation.Quantity = "10";
        Assert.True(row.IsResolved);

        allocation.Type = "scrap_allowance";
        Assert.False(row.IsResolved);
        allocation.Type = "scrapAllowance";
        Assert.True(row.IsResolved);
    }

    [Fact]
    public void Legacy_import_row_shows_batch_context_and_limits_route_operations_to_the_selected_case()
    {
        var owner = new LegacyExcelImportViewModel();
        var firstCase = new LegacyImportCaseCandidate("case-1", "PN-1", "First part", null, null, "Exact");
        var secondCase = new LegacyImportCaseCandidate("case-2", "PN-2", "Second part", null, null, "Exact");
        var firstRouteOperation = new LegacyImportCaseOperationCandidate(
            "route-1", "case-1", 10, "Mill", "fiveAxis", null, null, 1);
        var secondRouteOperation = new LegacyImportCaseOperationCandidate(
            "route-2", "case-2", 20, "Turn", "turning", null, null, 1);
        var batchOperation = new LegacyImportBatchOperationCandidate(
            BatchOperationId: "batch-operation-1",
            BatchId: "batch-1",
            BatchNumber: null,
            CaseId: "case-1",
            PartNumber: "PN-1",
            CaseOperationId: "route-1",
            OperationNumber: 10,
            Name: "Mill",
            Status: "not_started",
            RequiredMachineType: "fiveAxis",
            Version: 3,
            AssignmentId: null,
            MachineId: null,
            AssignmentVersion: null);
        var row = LegacyImportRowViewModel.Planning(new LegacyImportPlanningRow(
            "plan-1", "Planning", 5, "machine-a", 1,
            new LegacyImportPlanningValues("Acme", "PN-1", "Ref-1", null, 10, null, null, null, null, null),
            [], new LegacyImportPlanningCandidates(
                [firstCase, secondCase],
                [],
                [new LegacyImportBatchCandidate("batch-1", "B-104", 10, "Exact")],
                [firstRouteOperation, secondRouteOperation],
                [batchOperation])), [], owner);

        Assert.Empty(row.RouteOperationCandidates);
        Assert.Contains("Batch B-104 / PN-1", row.ExistingOperationCandidates.Single().DisplayName, StringComparison.Ordinal);

        row.SelectedCaseCandidate = firstCase;
        Assert.Equal([firstRouteOperation], row.RouteOperationCandidates);
        row.SelectedRouteOperationCandidate = firstRouteOperation;
        Assert.Equal("route-1", row.RouteOperation);

        row.SelectedCaseCandidate = secondCase;
        Assert.Null(row.SelectedRouteOperationCandidate);
        Assert.Equal(string.Empty, row.RouteOperation);
        Assert.Equal([secondRouteOperation], row.RouteOperationCandidates);

        row.SelectedRouteOperationCandidate = firstRouteOperation;
        Assert.Null(row.SelectedRouteOperationCandidate);
    }

    [Fact]
    public void Legacy_import_row_mirrors_complete_machine_compatibility_facts_and_requires_an_explicit_override()
    {
        var owner = new LegacyExcelImportViewModel();
        var compatibleMachine = new LegacyImportMachineCandidate(
            MachineId: "machine-compatible",
            Number: "M1",
            Name: "Five-axis mill",
            ProcessType: "milling",
            AxisType: "3-axis",
            Capabilities: [],
            MachineTypeCapabilities: ["fiveAxis"],
            Score: 1,
            Reason: "Exact");
        var incompatibleMachine = new LegacyImportMachineCandidate(
            MachineId: "machine-incompatible",
            Number: "M2",
            Name: "Lathe",
            ProcessType: "turning",
            AxisType: "2-axis",
            Capabilities: ["bar-feeder"],
            MachineTypeCapabilities: [],
            Score: 0.8m,
            Reason: "Manual choice");
        var mapping = LegacyImportMappingViewModel.Machine(new LegacyImportMachineSection(
            "machine-a", "Planning", 1, "Source mill", 2, 2, [compatibleMachine, incompatibleMachine]), owner);
        owner.MachineMappings.Add(mapping);
        mapping.SelectedMachineCandidate = compatibleMachine;

        var operation = new LegacyImportBatchOperationCandidate(
            BatchOperationId: "batch-operation-1",
            BatchId: "batch-1",
            BatchNumber: "B-104",
            CaseId: "case-1",
            PartNumber: "PN-1",
            CaseOperationId: "route-1",
            OperationNumber: 10,
            Name: "Finish milling",
            Status: "not_started",
            RequiredMachineType: "fiveAxis",
            Version: 1,
            AssignmentId: null,
            MachineId: null,
            AssignmentVersion: null);
        var row = LegacyImportRowViewModel.Planning(new LegacyImportPlanningRow(
            "plan-1", "Planning", 5, "machine-a", 1,
            new LegacyImportPlanningValues("Acme", "PN-1", "Ref-1", null, 10, null, null, null, null, null),
            [], new LegacyImportPlanningCandidates([], [], [], [], [operation])), [], owner);

        row.SelectedExistingOperationCandidate = operation;
        Assert.False(row.RequiresCompatibilityOverride);
        Assert.True(row.IsResolved);
        Assert.Contains("Machine-Type capability", row.CompatibilityReviewText, StringComparison.Ordinal);

        mapping.SelectedMachineCandidate = incompatibleMachine;
        Assert.True(row.RequiresCompatibilityOverride);
        Assert.False(row.IsResolved);
        Assert.Equal("Blocked", row.Status);
        Assert.Contains("fiveAxis", row.CompatibilityReviewText, StringComparison.Ordinal);
        Assert.Contains("M2", row.CompatibilityReviewText, StringComparison.Ordinal);

        row.CompatibilityOverrideConfirmed = true;
        Assert.False(row.IsResolved);
        row.CompatibilityOverrideReason = "Planner approved the cross-type assignment.";

        Assert.True(row.IsResolved);
        Assert.Equal("Warning", row.Status);
        var selection = row.ToPlanningSelection(incompatibleMachine.MachineId);
        Assert.True(selection.CompatibilityOverride!.Confirmed);
        Assert.Equal("Planner approved the cross-type assignment.", selection.CompatibilityOverride.Reason);
    }

    [Fact]
    public async Task Legacy_import_column_mapping_keeps_the_target_field_stable_and_commits_the_selected_source_column()
    {
        const string previewJson = """
            {"schemaVersion":1,"importToken":"import-1","workbookSha256":"abc123","expiresAt":"2026-08-20T10:00:00Z",
             "workbook":{"fileName":"legacy.xlsx","sheets":[{"name":"Planning","rowCount":2,"columnCount":3}]},
             "suggestions":{"planningSheet":"Planning","openOrdersSheet":null,"planningColumns":[{"field":"partNumber","column":"A","header":"Part","confidence":1}],"openOrderColumns":[]},
             "machineSections":[],"rows":[{"rowKey":"plan-1","sheetName":"Planning","rowNumber":2,"sectionKey":"none","sourceOrder":1,"values":{"partNumber":"PN-1","quantity":1},"provenance":[],"candidates":{"cases":[],"orders":[],"batches":[],"caseOperations":[],"batchOperations":[]}}],"openOrderRows":[{"rowKey":"open-1","sheetName":"Orders","rowNumber":2,"sourceOrder":1,"values":{"partNumber":"PN-2","orderNumber":"SO-2","customer":"Acme","outstandingQuantity":1},"provenance":[],"candidates":{"cases":[],"orders":[]}}],"issues":[]}
            """;
        const string receiptJson = """
            {"schemaVersion":1,"workbookSha256":"abc123","commitId":"commit-1","replayed":false,
             "created":{"caseIds":[],"orderIds":[],"batchIds":[],"assignmentIds":[]},
             "unchanged":{"caseIds":[],"orderIds":[],"batchIds":[],"assignmentIds":[]},"machineBacklogs":[]}
            """;
        var handler = new RecordingHandler(Json(HttpStatusCode.OK, previewJson), Json(HttpStatusCode.OK, receiptJson));
        using var api = CreateClient(handler);
        var viewModel = new LegacyExcelImportViewModel(_ => new MemoryStream([0x50, 0x4B]), _ => true);
        viewModel.AttachSession(api, "windows-1", new EditModeStatus(
            ClientEditState.Editor, 9, null, null, DateTimeOffset.UtcNow, 30));
        viewModel.SetWorkbookSelection("legacy.xlsx");
        await viewModel.PreviewAsync();

        var mapping = Assert.Single(viewModel.Mappings);
        Assert.Equal("partNumber", mapping.TargetField);
        Assert.Equal(["A", "B", "C"], mapping.ColumnChoices);
        mapping.SourceColumn = "D";
        Assert.False(mapping.IsResolved);
        mapping.SourceColumn = "C";
        viewModel.Rows.Single(row => row.Kind == "planning").IsSkipped = true;
        var openOrder = viewModel.Rows.Single(row => row.Kind == "open_orders");
        openOrder.Decision = "create_case";
        openOrder.NewCasePartNumber = "PN-2";
        openOrder.NewCaseName = "Imported part";
        openOrder.NewCaseWorkingFolderPath = "C:\\Cases\\PN-2";
        Assert.True(viewModel.CanCommit);
        await viewModel.CommitAsync();

        Assert.Contains("\"field\":\"partNumber\",\"column\":\"C\"", handler.Requests[1].Body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"field\":\"C\"", handler.Requests[1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Machine_downtime_client_uses_list_create_edit_and_restore_routes_with_authority()
    {
        const string downtime = """
            {"downtimeId":"down-1","machineId":"machine-1","downtimeType":"breakdown",
             "startsAt":"2026-08-11T10:00:00Z","endsAt":null,"reason":"Hydraulic leak",
             "plannedBy":null,"repairNote":null,"reportedBy":"Operator","status":"active",
             "version":1,"createdAt":"2026-08-11T10:00:00Z","updatedAt":"2026-08-11T10:00:00Z"}
            """;
        var handler = new RecordingHandler(
            Json(HttpStatusCode.OK, $$"""{"items":[{{downtime}}],"nextCursor":null}"""),
            Json(HttpStatusCode.Created, downtime),
            JsonWithEntityTag(HttpStatusCode.OK, downtime.Replace("\"breakdown\"", "\"planned_maintenance\"").Replace("\"plannedBy\":null", "\"plannedBy\":\"Planner\""), "\"downtime:down-1:v2\""),
            JsonWithEntityTag(HttpStatusCode.OK, downtime.Replace("\"endsAt\":null", "\"endsAt\":\"2026-08-11T11:00:00Z\"").Replace("\"status\":\"active\"", "\"status\":\"restored\""), "\"downtime:down-1:v2\""));
        using var api = CreateClient(handler);

        Assert.Single(await api.ListDowntimesAsync("machine-1"));
        await api.CreateDowntimeAsync(new("breakdown", "machine-1",
            DateTimeOffset.Parse("2026-08-11T10:00:00Z"), null, "Hydraulic leak", null, "Operator"),
            "client-1", 7);
        await api.UpdatePlannedMaintenanceAsync("down-1", new("machine-1",
            DateTimeOffset.Parse("2026-08-11T09:00:00Z"), DateTimeOffset.Parse("2026-08-11T10:00:00Z"),
            "Service", "Planner"), "\"downtime:down-1:v1\"", "client-1", 7);
        await api.RestoreBreakdownAsync("down-1", new(DateTimeOffset.Parse("2026-08-11T11:00:00Z"), "Repaired"),
            "\"downtime:down-1:v1\"", "client-1", 7);

        Assert.Equal("/api/v1/downtimes?machineId=machine-1", handler.Requests[0].Path);
        Assert.Equal("/api/v1/downtimes", handler.Requests[1].Path);
        Assert.Equal("7", handler.Requests[1].Generation);
        Assert.Equal(HttpMethod.Patch, handler.Requests[2].Method);
        Assert.Equal("\"downtime:down-1:v1\"", handler.Requests[2].IfMatch);
        Assert.Equal("/api/v1/downtimes/down-1/restore", handler.Requests[3].Path);
        Assert.Contains("\"repairNote\":\"Repaired\"", handler.Requests[3].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reads_health_and_edit_state_over_http_only()
    {
        var handler = new RecordingHandler(
            Json(HttpStatusCode.OK, """
                {
                  "status": "healthy",
                  "service": "Meimad Planner Server",
                  "version": "0.1.0",
                  "serverTimeUtc": "2026-08-11T10:00:00Z"
                }
                """),
            Json(HttpStatusCode.OK, EditJson("viewer", 4)));
        using var api = CreateClient(handler);

        var health = await api.GetHealthAsync();
        var edit = await api.GetEditModeAsync("windows-01");

        Assert.Equal("healthy", health.Status);
        Assert.Equal("Meimad Planner Server", health.Service);
        Assert.Equal(ClientEditState.Viewer, edit.State);
        Assert.Equal(4, edit.Generation);
        Assert.Equal("/health", handler.Requests[0].Path);
        Assert.Equal("/api/v1/edit-mode", handler.Requests[1].Path);
        Assert.Equal("windows-01", handler.Requests[1].ClientId);
    }

    [Fact]
    public async Task Edit_commands_send_client_user_generation_and_decision()
    {
        var handler = new RecordingHandler(
            Json(HttpStatusCode.Accepted, EditJson("requestingEdit", 7, includePending: true)),
            Json(HttpStatusCode.OK, EditJson("viewer", 8)),
            Json(HttpStatusCode.OK, EditJson("editor", 9)));
        using var api = CreateClient(handler);

        var requested = await api.RequestEditAsync("windows-02", "Local Planner");
        await api.ReleaseEditAsync("windows-02", 7);
        await api.DecideTransferAsync("windows-02", 8, "request/with slash", release: false);

        Assert.Equal(ClientEditState.RequestingEdit, requested.State);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("windows-02", handler.Requests[0].ClientId);
        Assert.Equal("Local Planner", handler.Requests[0].UserId);
        Assert.Equal("7", handler.Requests[1].Generation);
        Assert.Equal(
            "/api/v1/edit-mode/requests/request%2Fwith%20slash/decision",
            handler.Requests[2].Path);
        Assert.Equal("8", handler.Requests[2].Generation);
        Assert.Contains("\"decision\":\"reject\"", handler.Requests[2].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Safe_server_error_is_exposed_without_raw_content()
    {
        var handler = new RecordingHandler(Json(HttpStatusCode.Conflict, """
            {
              "error": {
                "code": "edit_request_pending",
                "message": "Another client is already waiting."
              }
            }
            """));
        using var api = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<PlannerApiException>(() =>
            api.RequestEditAsync("windows-03", "Planner"));

        Assert.Equal(HttpStatusCode.Conflict, exception.StatusCode);
        Assert.Equal("edit_request_pending", exception.Code);
        Assert.Equal("Another client is already waiting.", exception.Message);
    }

    [Fact]
    public async Task Case_pool_and_details_are_read_only_api_queries()
    {
        var handler = new RecordingHandler(
            Json(HttpStatusCode.OK, $$"""
                { "items": [ {{CaseJson("case-1")}} ], "nextCursor": null }
                """),
            JsonWithEntityTag(HttpStatusCode.OK, CaseJson("case-1"), "\"case:case-1:v3\""),
            Json(HttpStatusCode.OK, """{ "items": [], "nextCursor": null }"""),
            Json(HttpStatusCode.OK, """{ "items": [], "nextCursor": null }"""),
            Json(HttpStatusCode.OK, """{ "items": [], "nextCursor": null }"""),
            new HttpResponseMessage(HttpStatusCode.NotFound));
        using var api = CreateClient(handler);

        var cases = await api.ListCasesAsync(new CaseQuery("PN / 1", "Acme & Co", true));
        var resource = await api.GetCaseAsync("case-1");
        await api.ListCaseOperationsAsync("case-1");
        await api.ListOrdersAsync("case-1");
        await api.ListBatchesAsync("case-1");
        var preview = await api.GetCasePreviewAsync("case-1");

        Assert.Single(cases);
        Assert.Equal("PN-1", resource.Value.PartNumber);
        Assert.Equal("\"case:case-1:v3\"", resource.EntityTag);
        Assert.Null(preview);
        Assert.Equal(
            "/api/v1/cases?search=PN%20%2F%201&customer=Acme%20%26%20Co&isActive=true",
            handler.Requests[0].Path);
        Assert.Equal("/api/v1/cases/case-1/operations", handler.Requests[2].Path);
        Assert.Equal("/api/v1/orders?caseId=case-1", handler.Requests[3].Path);
        Assert.Equal("/api/v1/batches?caseId=case-1", handler.Requests[4].Path);
        Assert.Equal("/api/v1/cases/case-1/preview", handler.Requests[5].Path);
    }

    [Fact]
    public async Task Case_save_sends_edit_generation_and_etag()
    {
        var handler = new RecordingHandler(
            JsonWithEntityTag(HttpStatusCode.OK, CaseJson("case-2"), "\"case:case-2:v4\""));
        using var api = CreateClient(handler);

        var result = await api.UpdateCaseAsync(
            "case-2",
            new CaseUpdate(
                "PN-1", "Part", null, "Acme", null, null, "C:\\Cases\\PN-1",
                null, null, null, null, null),
            "\"case:case-2:v3\"",
            "windows-01",
            9);

        Assert.Equal("\"case:case-2:v4\"", result.EntityTag);
        Assert.Equal(HttpMethod.Patch, handler.Requests[0].Method);
        Assert.Equal("windows-01", handler.Requests[0].ClientId);
        Assert.Equal("9", handler.Requests[0].Generation);
        Assert.Equal("\"case:case-2:v3\"", handler.Requests[0].IfMatch);
        Assert.Contains("\"partNumber\":\"PN-1\"", handler.Requests[0].Body, StringComparison.Ordinal);
        Assert.DoesNotContain("currentSetupTimeSeconds", handler.Requests[0].Body, StringComparison.Ordinal);
        Assert.DoesNotContain("currentCycleTimePerPartSeconds", handler.Requests[0].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Case_create_sends_all_master_paths_and_edit_generation()
    {
        var handler = new RecordingHandler(
            JsonWithEntityTag(HttpStatusCode.Created, CaseJson("case-new"), "\"case:case-new:v1\""));
        using var api = CreateClient(handler);

        var result = await api.CreateCaseAsync(
            new CaseUpdate(
                "PN-NEW", "New Part", "A", "Acme", "PO-77",
                @"C:\Cases\PN-NEW\picture.png", @"C:\Cases\PN-NEW",
                "Aluminium", "7075-T6", "Plate", "20 x 80 x 100",
                "New Case"),
            "windows-01",
            13);

        Assert.Equal("case-new", result.Value.CaseId);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("/api/v1/cases", handler.Requests[0].Path);
        Assert.Equal("windows-01", handler.Requests[0].ClientId);
        Assert.Equal("13", handler.Requests[0].Generation);
        Assert.Contains("\"workingFolderPath\":\"C:\\\\Cases\\\\PN-NEW\"", handler.Requests[0].Body, StringComparison.Ordinal);
        Assert.Contains("\"previewPath\":\"C:\\\\Cases\\\\PN-NEW\\\\picture.png\"", handler.Requests[0].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Machine_create_and_picture_use_server_api_only()
    {
        var picture = new byte[] { 1, 2, 3, 4 };
        var handler = new RecordingHandler(
            Json(HttpStatusCode.Created, MachineJson("machine-new")),
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(picture)
            });
        using var api = CreateClient(handler);

        var created = await api.CreateMachineAsync(
            new MachineCreate(
                "M-21", "Mill 21", "mill", "5-axis", ["probe", "high-speed"],
                "calendar-day", true, true, @"C:\MachinePictures\M-21.jpg"),
            "windows-01",
            14);
        var bytes = await api.GetMachinePictureAsync(created.MachineId);

        Assert.Equal("machine-new", created.MachineId);
        Assert.Equal(picture, bytes);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("/api/v1/machines", handler.Requests[0].Path);
        Assert.Equal("14", handler.Requests[0].Generation);
        Assert.Contains("\"workingCalendarId\":\"calendar-day\"", handler.Requests[0].Body, StringComparison.Ordinal);
        Assert.Contains("\"picturePath\":\"C:\\\\MachinePictures\\\\M-21.jpg\"", handler.Requests[0].Body, StringComparison.Ordinal);
        Assert.Equal("/api/v1/machines/machine-new/picture", handler.Requests[1].Path);
    }

    [Fact]
    public async Task Machine_list_reads_compatibility_values_from_server()
    {
        var handler = new RecordingHandler(Json(HttpStatusCode.OK, $$"""
            { "items": [{{MachineJson("machine-1")}}], "nextCursor": null }
            """));
        using var api = CreateClient(handler);

        var machine = Assert.Single(await api.ListMachinesAsync());

        Assert.Equal("mill", machine.ProcessType);
        Assert.Equal("5-axis", machine.AxisType);
        Assert.Contains("probe", machine.Capabilities);
        Assert.Equal("/api/v1/machines", handler.Requests[0].Path);
    }

    [Fact]
    public async Task Machine_update_and_guarded_delete_commands_use_server_api()
    {
        var handler = new RecordingHandler(
            JsonWithEntityTag(HttpStatusCode.OK, MachineJson("machine-1"), "\"machine:machine-1:v1\""),
            JsonWithEntityTag(HttpStatusCode.OK, MachineJson("machine-1"), "\"machine:machine-1:v2\""),
            new HttpResponseMessage(HttpStatusCode.NoContent),
            new HttpResponseMessage(HttpStatusCode.NoContent),
            new HttpResponseMessage(HttpStatusCode.NoContent),
            new HttpResponseMessage(HttpStatusCode.NoContent),
            new HttpResponseMessage(HttpStatusCode.NoContent));
        using var api = CreateClient(handler);

        var resource = await api.GetMachineAsync("machine-1");
        await api.UpdateMachineAsync(
            "machine-1",
            new MachineCreate("M-21", "Updated", "mill", "5-axis", ["probe"], "calendar-day", true, true, null),
            resource.EntityTag, "windows-01", 30);
        await api.DeleteCaseAsync("case-1", "windows-01", 30);
        await api.DeleteCaseOperationAsync("case-1", "op-1", "windows-01", 30);
        await api.DeleteOrderAsync("order-1", "windows-01", 30);
        await api.DeleteBatchAsync("batch-1", "windows-01", 30);
        await api.DeleteMachineAsync("machine-1", "windows-01", 30);

        Assert.Equal(HttpMethod.Patch, handler.Requests[1].Method);
        Assert.Equal("\"machine:machine-1:v1\"", handler.Requests[1].IfMatch);
        Assert.All(handler.Requests.Skip(2), request => Assert.Equal(HttpMethod.Delete, request.Method));
        Assert.Equal("/api/v1/cases/case-1/operations/op-1", handler.Requests[3].Path);
        Assert.All(handler.Requests.Skip(1), request => Assert.Equal("30", request.Generation));
    }

    [Fact]
    public async Task Operation_execution_command_uses_server_api_and_edit_generation()
    {
        var handler = new RecordingHandler(Json(HttpStatusCode.OK, """
            {
              "batchOperationId":"operation-1",
              "machineId":"machine-1",
              "status":"in_progress",
              "version":2
            }
            """), Json(HttpStatusCode.OK, """
            {
              "batchOperationId":"operation-1",
              "machineId":"machine-1",
              "status":"not_started",
              "version":3
            }
            """));
        using var api = CreateClient(handler);

        var result = await api.ChangeOperationExecutionAsync(
            "operation/1", "start", "windows-01", 21);

        Assert.Equal("in_progress", result.Status);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("/api/v1/batch-operations/operation%2F1/start", handler.Requests[0].Path);
        Assert.Equal("windows-01", handler.Requests[0].ClientId);
        Assert.Equal("21", handler.Requests[0].Generation);

        var reset = await api.ChangeOperationExecutionAsync(
            "operation/1", "reset", "windows-01", 21);
        Assert.Equal("not_started", reset.Status);
        Assert.Equal("/api/v1/batch-operations/operation%2F1/reset", handler.Requests[1].Path);
    }

    [Fact]
    public async Task Working_calendar_list_and_create_use_server_api_and_edit_authority()
    {
        var handler = new RecordingHandler(
            Json(HttpStatusCode.OK, $$"""{ "items": [ {{CalendarJson("calendar-day", "Day shift")}} ], "nextCursor": null }"""),
            Json(HttpStatusCode.Created, CalendarJson("calendar-new", "Extended shift")));
        using var api = CreateClient(handler);

        var calendars = await api.ListWorkingCalendarsAsync();
        var created = await api.CreateWorkingCalendarAsync(
            new WorkingCalendarCreate(
                "Extended shift", "Asia/Jerusalem",
                ["sunday", "monday", "tuesday", "wednesday", "thursday"],
                null, null,
                [new WorkingCalendarWindow("06:00", "22:00")],
                [new WorkingCalendarWindow("12:00", "12:30")],
                [new WorkingCalendarException("2026-09-13", [], [], "Closed")],
                ["machine", "setup_worker"]),
            "windows-01",
            19);

        Assert.Equal("calendar-day", calendars.Single().WorkingCalendarId);
        Assert.Equal("calendar-new", created.WorkingCalendarId);
        Assert.Equal("/api/v1/working-calendars", handler.Requests[0].Path);
        Assert.Equal(HttpMethod.Post, handler.Requests[1].Method);
        Assert.Equal("windows-01", handler.Requests[1].ClientId);
        Assert.Equal("19", handler.Requests[1].Generation);
        Assert.Contains("\"breakWindows\":[{\"startsAtLocal\":\"12:00\",\"endsAtLocal\":\"12:30\"}]", handler.Requests[1].Body, StringComparison.Ordinal);
        Assert.Contains("\"exceptions\":[{\"date\":\"2026-09-13\"", handler.Requests[1].Body, StringComparison.Ordinal);
        Assert.Contains("\"usages\":[\"machine\",\"setup_worker\"]", handler.Requests[1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Order_and_batch_create_send_edit_authority_and_explicit_allocations()
    {
        var handler = new RecordingHandler(
            Json(HttpStatusCode.Created, """
                {
                  "orderId":"order-2","caseId":"case-1","orderNumber":"SO-2",
                  "quantity":12,"workFinishDate":"2026-09-15","status":"active","notes":"Priority"
                }
                """),
            Json(HttpStatusCode.Created, """
                {
                  "batchId":"batch-2","caseId":"case-1","batchNumber":"B-2",
                  "status":"waiting","plannedQuantity":15,"routeRevision":3,
                  "allocations":[],"batchOperationCount":4
                }
                """));
        using var api = CreateClient(handler);

        var order = await api.CreateOrderAsync(
            new OrderCreate("case-1", "SO-2", 12, "2026-09-15", "active", "Priority"),
            "windows-01",
            18);
        var batch = await api.CreateBatchAsync(
            new ProductionBatchCreate(
                "case-1",
                "B-2",
                "waiting",
                15,
                [
                    new BatchAllocationCreate("order", "order-1", 5),
                    new BatchAllocationCreate("order", "order-2", 7),
                    new BatchAllocationCreate("stock", null, 2),
                    new BatchAllocationCreate("scrapAllowance", null, 1)
                ]),
            "windows-01",
            18);

        Assert.Equal("order-2", order.OrderId);
        Assert.Equal(4, batch.BatchOperationCount);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("/api/v1/orders", handler.Requests[0].Path);
        Assert.Equal("windows-01", handler.Requests[0].ClientId);
        Assert.Equal("18", handler.Requests[0].Generation);
        Assert.Contains("\"workFinishDate\":\"2026-09-15\"", handler.Requests[0].Body, StringComparison.Ordinal);
        Assert.Equal(HttpMethod.Post, handler.Requests[1].Method);
        Assert.Equal("/api/v1/batches", handler.Requests[1].Path);
        Assert.Equal("18", handler.Requests[1].Generation);
        Assert.Contains("\"allocationType\":\"order\",\"orderId\":\"order-2\",\"quantity\":7", handler.Requests[1].Body, StringComparison.Ordinal);
        Assert.Contains("\"allocationType\":\"stock\",\"orderId\":null,\"quantity\":2", handler.Requests[1].Body, StringComparison.Ordinal);
        Assert.Contains("\"allocationType\":\"scrapAllowance\",\"orderId\":null,\"quantity\":1", handler.Requests[1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Order_update_sends_patch_etag_edit_authority_and_complete_editable_fields()
    {
        var handler = new RecordingHandler(Json(HttpStatusCode.OK, """
            {
              "orderId":"order/1","caseId":"case-1","orderNumber":"SO-1-REVISED",
              "quantity":8,"workFinishDate":"2026-09-01","status":"in_production",
              "notes":"Planner revision","version":2
            }
            """));
        using var api = CreateClient(handler);

        var updated = await api.UpdateOrderAsync(
            "order/1",
            new OrderUpdate(
                "SO-1-REVISED",
                8,
                "2026-09-01",
                "in_production",
                "Planner revision"),
            "\"order:order/1:v1\"",
            "windows-01",
            27);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Patch, request.Method);
        Assert.Equal("/api/v1/orders/order%2F1", request.Path);
        Assert.Equal("windows-01", request.ClientId);
        Assert.Equal("27", request.Generation);
        Assert.Equal("\"order:order/1:v1\"", request.IfMatch);
        Assert.Contains("\"orderNumber\":\"SO-1-REVISED\"", request.Body, StringComparison.Ordinal);
        Assert.Contains("\"quantity\":8", request.Body, StringComparison.Ordinal);
        Assert.Contains("\"workFinishDate\":\"2026-09-01\"", request.Body, StringComparison.Ordinal);
        Assert.Contains("\"status\":\"in_production\"", request.Body, StringComparison.Ordinal);
        Assert.Contains("\"notes\":\"Planner revision\"", request.Body, StringComparison.Ordinal);
        Assert.Equal(2, updated.Version);
    }

    [Fact]
    public async Task Order_update_omits_null_status_to_preserve_server_derived_lifecycle_authority()
    {
        var handler = new RecordingHandler(Json(HttpStatusCode.OK, """
            {
              "orderId":"order-1","caseId":"case-1","orderNumber":"SO-1",
              "quantity":8,"workFinishDate":"2026-09-01","status":"in_production",
              "notes":null,"version":2
            }
            """));
        using var api = CreateClient(handler);

        await api.UpdateOrderAsync(
            "order-1",
            new OrderUpdate("SO-1", 8, "2026-09-01", null, null),
            "\"order:order-1:v1\"",
            "windows-01",
            27);

        var request = Assert.Single(handler.Requests);
        Assert.DoesNotContain("\"status\"", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Case_operation_create_uses_nested_route_and_complete_dependency_payload()
    {
        var handler = new RecordingHandler(Json(HttpStatusCode.Created, """
            {
              "caseOperationId":"operation-20","caseId":"case-1",
              "operationNumber":20,"routePosition":1,"name":"Finish mill",
              "requiredMachineType":"fiveAxisMill","setupTimeSeconds":120,
              "cycleTimePerPartSeconds":45,"dependencyType":"SEQUENTIAL",
              "predecessorCaseOperationId":"operation-10","simultaneousGroupKey":null,
              "version":1,"createdAt":"2026-08-11T10:00:00Z","updatedAt":"2026-08-11T10:00:00Z"
            }
            """));
        using var api = CreateClient(handler);

        var created = await api.CreateCaseOperationAsync(
            "case-1",
            new CaseOperationCreate(
                20,
                "Finish mill",
                "fiveAxisMill",
                120,
                45,
                "SEQUENTIAL",
                "operation-10",
                null),
            "windows-01",
            22);

        Assert.Equal("operation-20", created.CaseOperationId);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("/api/v1/cases/case-1/operations", handler.Requests[0].Path);
        Assert.Equal("windows-01", handler.Requests[0].ClientId);
        Assert.Equal("22", handler.Requests[0].Generation);
        Assert.Contains("\"dependencyType\":\"SEQUENTIAL\"", handler.Requests[0].Body, StringComparison.Ordinal);
        Assert.Contains("\"predecessorCaseOperationId\":\"operation-10\"", handler.Requests[0].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Case_operation_update_uses_nested_patch_edit_authority_and_etag()
    {
        var handler = new RecordingHandler(Json(HttpStatusCode.OK, """
            {
              "caseOperationId":"operation-20","caseId":"case-1",
              "operationNumber":20,"routePosition":1,"name":"Finish mill revised",
              "requiredMachineType":"5-axis","setupTimeSeconds":3723,
              "cycleTimePerPartSeconds":45,"dependencyType":"SEQUENTIAL",
              "predecessorCaseOperationId":"operation-10","simultaneousGroupKey":null,
              "version":4
            }
            """));
        using var api = CreateClient(handler);

        var updated = await api.UpdateCaseOperationAsync(
            "case-1",
            "operation-20",
            new CaseOperationUpdate(
                20, "Finish mill revised", "5-axis", 3723, 45,
                "SEQUENTIAL", "operation-10", null),
            "\"case-operation:operation-20:v3\"",
            "windows-01",
            23);

        Assert.Equal(4, updated.Version);
        Assert.Equal(HttpMethod.Patch, handler.Requests[0].Method);
        Assert.Equal("/api/v1/cases/case-1/operations/operation-20", handler.Requests[0].Path);
        Assert.Equal("windows-01", handler.Requests[0].ClientId);
        Assert.Equal("23", handler.Requests[0].Generation);
        Assert.Equal("\"case-operation:operation-20:v3\"", handler.Requests[0].IfMatch);
        Assert.Contains("\"setupTimeSeconds\":3723", handler.Requests[0].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Planning_board_and_manual_assignment_use_only_documented_http_routes()
    {
        var handler = new RecordingHandler(
            Json(HttpStatusCode.OK, """
                {
                  "readAt": "2026-08-11T10:00:00Z",
                  "conflictCalculationStatus": "unavailable",
                  "conflictCalculationMessage": "The pure time engine is not connected to the planning-board projection yet.",
                  "conflicts": [],
                  "pool": [{
                    "batchOperationId":"op-compact","batchId":"batch-1","batchNumber":"B-1",
                    "caseId":"case-1","caseName":"Widget case","partNumber":"PN-1","operationNumber":10,
                    "operationName":"Mill","requiredMachineType":"mill",
                    "setupTimeSeconds":600,"cycleTimePerPartSeconds":120,
                    "status":"not_started","machineId":null,"backlogPosition":null,
                    "plannedQuantity":4,"orderReferences":["SO-1"],"estimatedTimeSeconds":1080
                  }],
                  "machines": []
                }
                """),
            Json(HttpStatusCode.Created, "{}"),
            new HttpResponseMessage(HttpStatusCode.NoContent));
        using var api = CreateClient(handler);

        var board = await api.GetPlanningBoardAsync();
        await api.AssignOrMoveOperationAsync("op/1", "machine-1", 3, "windows-1", 12);
        await api.UnassignOperationAsync("op/1", "windows-1", 12);

        Assert.Equal("unavailable", board.ConflictCalculationStatus);
        var operation = Assert.Single(board.Pool);
        Assert.Equal(4, operation.PlannedQuantity);
        Assert.Equal("SO-1", Assert.Single(operation.OrderReferences!));
        Assert.Equal(1_080, operation.EstimatedTimeSeconds);
        Assert.Equal("Widget case", operation.CaseName);
        Assert.Null(operation.MachineAssignmentId);
        Assert.Null(operation.AssignmentVersion);
        Assert.Equal("manual", operation.PlanningMode);
        Assert.Equal("/api/v1/planning-board", handler.Requests[0].Path);
        Assert.Equal(HttpMethod.Put, handler.Requests[1].Method);
        Assert.Equal("/api/v1/batch-operations/op%2F1/assignment", handler.Requests[1].Path);
        Assert.Equal("windows-1", handler.Requests[1].ClientId);
        Assert.Equal("12", handler.Requests[1].Generation);
        Assert.Contains("\"machineId\":\"machine-1\"", handler.Requests[1].Body, StringComparison.Ordinal);
        Assert.Contains("\"backlogPosition\":3", handler.Requests[1].Body, StringComparison.Ordinal);
        Assert.Equal(HttpMethod.Delete, handler.Requests[2].Method);
        Assert.Equal("/api/v1/batch-operations/op%2F1/assignment", handler.Requests[2].Path);
    }

    [Fact]
    public async Task Timeline_reads_server_projection_with_utc_horizon()
    {
        var handler = new RecordingHandler(Json(HttpStatusCode.OK, """
            {
              "readAt": "2026-08-11T10:00:00Z",
              "horizonStart": "2026-08-11T08:00:00Z",
              "horizonEnd": "2026-08-12T08:00:00Z",
              "batches": [{"batchId":"batch-1","batchNumber":"B-1","partNumber":"PN-1"}],
              "machines": [{
                "machineId":"machine-1","number":"M-1","name":"Mill",
                "intervals":[{
                  "type":"operation","machineId":"machine-1","operationId":"op-1",
                  "batchId":"batch-1","batchNumber":"B-1","partNumber":"PN-1",
                  "operationNumber":10,"startsAt":"2026-08-11T08:00:00Z",
                  "endsAt":"2026-08-11T09:00:00Z","detail":null
                }]
              }],
              "dependencies": [],
              "conflicts": []
            }
            """));
        using var api = CreateClient(handler);

        var result = await api.GetTimelineAsync(
            DateTimeOffset.Parse("2026-08-11T11:00:00+03:00"),
            DateTimeOffset.Parse("2026-08-12T11:00:00+03:00"));

        Assert.Equal("operation", result.Machines[0].Intervals[0].Type);
        Assert.Equal("B-1", result.Batches[0].BatchNumber);
        Assert.Contains("from=2026-08-11T08%3A00%3A00", handler.Requests[0].Path, StringComparison.Ordinal);
        Assert.Contains("to=2026-08-12T08%3A00%3A00", handler.Requests[0].Path, StringComparison.Ordinal);
        Assert.DoesNotContain("mode=", handler.Requests[0].Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Timeline_reads_per_assignment_mode_and_due_date_without_a_global_mode_query()
    {
        var handler = new RecordingHandler(Json(HttpStatusCode.OK, """
            {
              "readAt": "2026-08-11T10:00:00Z",
              "horizonStart": "2026-08-11T08:00:00Z",
              "horizonEnd": "2026-08-20T08:00:00Z",
              "batches": [{
                "batchId":"batch-1","batchNumber":"B-1","partNumber":"PN-1",
                "workFinishDate":"2026-08-19"
              }],
              "machines": [{
                "machineId":"machine-1","number":"M-1","name":"Mill",
                "intervals":[{
                  "type":"operation","machineId":"machine-1","operationId":"op-1",
                  "batchId":"batch-1","batchNumber":"B-1","partNumber":"PN-1",
                  "operationNumber":10,"operationName":"Mill",
                  "startsAt":"2026-08-18T08:00:00Z","endsAt":"2026-08-18T09:00:00Z",
                  "detail":null,"planningMode":"backward",
                  "machineAssignmentId":"assignment-1","workFinishDate":"2026-08-19",
                  "phases":[
                    {"type":"setup","startsAt":"2026-08-18T08:00:00Z","endsAt":"2026-08-18T08:15:00Z","detail":"Setup"},
                    {"type":"loadunload","startsAt":"2026-08-18T08:15:00Z","endsAt":"2026-08-18T08:20:00Z","detail":"Part reload 1"},
                    {"type":"production","startsAt":"2026-08-18T08:20:00Z","endsAt":"2026-08-18T08:40:00Z","detail":null},
                    {"type":"loadunload","startsAt":"2026-08-18T08:40:00Z","endsAt":"2026-08-18T08:45:00Z","detail":"Part reload 2"},
                    {"type":"production","startsAt":"2026-08-18T08:45:00Z","endsAt":"2026-08-18T09:00:00Z","detail":null}
                  ]
                }]
              }],
              "dependencies": [], "conflicts": []
            }
            """));
        using var api = CreateClient(handler);

        var result = await api.GetTimelineAsync(
            DateTimeOffset.Parse("2026-08-11T08:00:00Z"),
            DateTimeOffset.Parse("2026-08-20T08:00:00Z"),
            DateTimeOffset.Parse("2026-08-11T10:30:00+03:00"));

        var request = Assert.Single(handler.Requests);
        Assert.DoesNotContain("mode=", request.Path, StringComparison.Ordinal);
        Assert.Contains("asOf=2026-08-11T07%3A30%3A00", request.Path, StringComparison.Ordinal);
        Assert.Null(request.ClientId);
        Assert.Null(request.Generation);
        Assert.Equal(new DateOnly(2026, 8, 19), result.Batches[0].WorkFinishDate);
        Assert.Equal("Backward", result.Machines[0].Intervals[0].PlanningModeLabel);
        Assert.Equal("assignment-1", result.Machines[0].Intervals[0].MachineAssignmentId);
        Assert.Equal(new DateOnly(2026, 8, 19), result.Machines[0].Intervals[0].WorkFinishDate);
        var phases = result.Machines[0].Intervals[0].Phases!;
        Assert.Equal(["setup", "loadunload", "production", "loadunload", "production"],
            phases.Select(phase => phase.Type));
        Assert.Equal(["Part reload 1", "Part reload 2"],
            phases.Where(phase => phase.Type == "loadunload").Select(phase => phase.Detail));
        Assert.True(phases[1].StartsAt < phases[3].StartsAt);
    }

    [Fact]
    public async Task Planning_mode_patch_targets_the_existing_assignment_with_concurrency_headers()
    {
        var handler = new RecordingHandler(Json(HttpStatusCode.OK, """
            {
              "machineAssignmentId":"assignment/1",
              "batchOperationId":"operation-1",
              "machineId":"machine-1",
              "backlogPosition":2,
              "version":8,
              "createdAt":"2026-08-11T08:00:00Z",
              "updatedAt":"2026-08-13T08:00:00Z",
              "planningMode":"backward"
            }
            """));
        using var api = CreateClient(handler);

        var result = await api.ChangeMachineAssignmentPlanningModeAsync(
            "assignment/1", 7, "BACKWARD", "windows-1", 19);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Patch, request.Method);
        Assert.Equal("/api/v1/machine-assignments/assignment%2F1", request.Path);
        Assert.Equal("\"machine-assignment:assignment/1:v7\"", request.IfMatch);
        Assert.Equal("windows-1", request.ClientId);
        Assert.Equal("19", request.Generation);
        Assert.Equal("{\"planningMode\":\"backward\"}", request.Body);
        Assert.Equal("assignment/1", result.MachineAssignmentId);
        Assert.Equal("operation-1", result.BatchOperationId);
        Assert.Equal("backward", result.PlanningMode);
        Assert.Equal(8, result.Version);
    }

    [Fact]
    public async Task Timeline_can_send_an_optional_utc_as_of_time_for_deterministic_calculation()
    {
        var handler = new RecordingHandler(Json(HttpStatusCode.OK, """
            {
              "readAt": "2026-08-11T10:00:00Z",
              "horizonStart": "2026-08-11T08:00:00Z",
              "horizonEnd": "2026-08-12T08:00:00Z",
              "batches": [], "machines": [], "dependencies": [], "conflicts": []
            }
            """));
        using var api = CreateClient(handler);

        await api.GetTimelineAsync(
            DateTimeOffset.Parse("2026-08-11T08:00:00Z"),
            DateTimeOffset.Parse("2026-08-12T08:00:00Z"),
            DateTimeOffset.Parse("2026-08-11T10:30:00+03:00"));

        Assert.Contains("asOf=2026-08-11T07%3A30%3A00", handler.Requests[0].Path, StringComparison.Ordinal);
    }

    [Fact]
    public void Client_assembly_has_no_sqlite_reference()
    {
        var references = typeof(PlannerApiClient).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .ToArray();

        Assert.DoesNotContain(references, name =>
            name?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public async Task Cross_type_assignment_sends_explicit_confirmation_and_reason()
    {
        var handler = new RecordingHandler(Json(HttpStatusCode.Created, "{}"));
        using var api = CreateClient(handler);

        await api.AssignOrMoveOperationAsync(
            "op-1",
            "machine-5-axis",
            0,
            "windows-1",
            14,
            new MachineAssignmentCompatibilityOverride(true, "3-axis Machine unavailable"));

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Contains("\"compatibilityOverride\":{", request.Body, StringComparison.Ordinal);
        Assert.Contains("\"confirmed\":true", request.Body, StringComparison.Ordinal);
        Assert.Contains(
            "\"reason\":\"3-axis Machine unavailable\"",
            request.Body,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cross_type_warning_preserves_authoritative_server_types()
    {
        var handler = new RecordingHandler(Json(HttpStatusCode.Conflict, """
            {"error":{"code":"machine_type_override_required","message":"Confirm override.","details":[{"requiredMachineType":"3-axis","selectedMachineType":"5-axis milling"}]}}
            """));
        using var api = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<PlannerApiException>(() =>
            api.AssignOrMoveOperationAsync("op-1", "machine-1", 0, "windows-1", 14));

        Assert.Equal("3-axis", exception.RequiredMachineType);
        Assert.Equal("5-axis milling", exception.SelectedMachineType);
    }

    [Fact]
    public async Task Setup_resource_holiday_and_report_settings_use_server_routes_etags_and_edit_authority()
    {
        var handler = new RecordingHandler(
            Json(HttpStatusCode.Created, """
                {"resourceId":"resource-1","employeeNumber":"E-1","name":"Dana Bar","firstName":"Dana","lastName":"Bar","role":"regular_worker","skills":["inspection"],"assignedCalendarId":"calendar-1","photoPath":"C:\\photos\\dana.jpg","notes":"QA backup","email":"dana@example.test","isActive":true,"version":1,"createdAt":"2026-08-12T10:00:00Z","updatedAt":"2026-08-12T10:00:00Z"}
                """),
            JsonWithEntityTag(HttpStatusCode.OK, """
                {"resourceId":"resource-1","employeeNumber":"E-1","name":"Dana Katz","firstName":"Dana","lastName":"Katz","role":"regular_worker","skills":["inspection"],"assignedCalendarId":"calendar-1","photoPath":"C:\\photos\\dana.jpg","notes":"QA backup","email":"dana.katz@example.test","isActive":false,"version":2,"createdAt":"2026-08-12T10:00:00Z","updatedAt":"2026-08-12T10:01:00Z"}
                """, "\"resource:resource-1:v2\""),
            JsonWithEntityTag(HttpStatusCode.OK, """
                {"israeliHolidayId":"holiday-1","date":"2026-09-15","name":"Rosh Hashanah","version":2,"createdAt":"2026-08-12T10:00:00Z","updatedAt":"2026-08-12T10:00:00Z"}
                """, "\"israeli-holiday:holiday-1:v2\""),
            JsonWithEntityTag(HttpStatusCode.OK, """
                {"senderAddress":"reports@example.test","recipients":["manager@example.test"],"smtpHost":"smtp.example.test","smtpPort":587,"useSsl":true,"dailyReportEnabled":true,"dailyReportTimeLocal":"07:00","timeZoneId":"Asia/Jerusalem","version":3,"updatedAt":"2026-08-12T10:00:00Z"}
                """, "\"report-email-settings:1:v3\""),
            Json(HttpStatusCode.OK, """
                {"succeeded":true,"provider":"hebcal","fromYear":2026,"toYear":2027,"created":12,"updated":3,"preservedManual":2,"lastAttemptAt":"2026-08-12T10:00:00Z","lastSuccessAt":"2026-08-12T10:00:00Z","error":null}
                """));
        using var api = CreateClient(handler);

        var resource = await api.CreateResourceAsync(
            new ResourceCreate("E-1", "Dana", "Bar", "regular_worker", ["inspection"], "calendar-1", "C:\\photos\\dana.jpg", "QA backup", "dana@example.test", true), "windows-1", 31);
        var updatedResource = await api.UpdateResourceAsync(
            "resource-1", new ResourceUpdate("E-1", "Dana", "Katz", "regular_worker", ["inspection"],
                "calendar-1", "C:\\photos\\dana.jpg", "QA backup", "dana.katz@example.test", false),
            "\"resource:resource-1:v1\"", "windows-1", 31);
        var holiday = await api.UpdateIsraeliHolidayAsync(
            "holiday-1", new IsraeliHolidayUpdate("2026-09-15", "Rosh Hashanah"),
            "\"israeli-holiday:holiday-1:v1\"", "windows-1", 31);
        var settings = await api.UpdateReportEmailSettingsAsync(
            new ReportEmailSettingsUpdate("reports@example.test", ["manager@example.test"], "smtp.example.test", 587, true, true, "07:00", "Asia/Jerusalem"),
            "\"report-email-settings:1:v2\"", "windows-1", 31);
        var sync = await api.SynchronizeIsraeliHolidaysAsync(new(2026,2027),"windows-1",31);

        Assert.Equal("resource-1", resource.ResourceId);
        Assert.Equal("Dana Katz", updatedResource.Value.Name);
        Assert.Equal("\"resource:resource-1:v2\"", updatedResource.EntityTag);
        Assert.Equal("\"israeli-holiday:holiday-1:v2\"", holiday.EntityTag);
        Assert.Equal("\"report-email-settings:1:v3\"", settings.EntityTag);
        Assert.True(sync.Succeeded);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("/api/v1/resources", handler.Requests[0].Path);
        Assert.Equal("31", handler.Requests[0].Generation);
        Assert.Equal(HttpMethod.Patch, handler.Requests[1].Method);
        Assert.Equal("/api/v1/resources/resource-1", handler.Requests[1].Path);
        Assert.Equal("\"resource:resource-1:v1\"", handler.Requests[1].IfMatch);
        Assert.Contains("\"lastName\":\"Katz\"", handler.Requests[1].Body, StringComparison.Ordinal);
        Assert.Equal(HttpMethod.Patch, handler.Requests[2].Method);
        Assert.Equal("/api/v1/israeli-holidays/holiday-1", handler.Requests[2].Path);
        Assert.Equal("\"israeli-holiday:holiday-1:v1\"", handler.Requests[2].IfMatch);
        Assert.Equal(HttpMethod.Put, handler.Requests[3].Method);
        Assert.Equal("/api/v1/report-email-settings", handler.Requests[3].Path);
        Assert.Equal("\"report-email-settings:1:v2\"", handler.Requests[3].IfMatch);
        Assert.Contains("\"recipients\":[\"manager@example.test\"]", handler.Requests[3].Body, StringComparison.Ordinal);
        Assert.Equal(HttpMethod.Post,handler.Requests[4].Method);
        Assert.Equal("/api/v1/israeli-holidays/sync",handler.Requests[4].Path);
        Assert.Contains("\"fromYear\":2026",handler.Requests[4].Body,StringComparison.Ordinal);
    }

    [Fact]
    public async Task Employee_exception_and_availability_routes_use_typed_contracts()
    {
        const string exceptionJson = """
            {"exceptionId":"exception-1","resourceId":"resource-1","date":"2026-08-18","exceptionType":"unavailable","isFullDay":false,"startsAtLocal":"10:00","endsAtLocal":"12:00","note":"Appointment","version":1,"createdAt":"2026-08-12T10:00:00Z","updatedAt":"2026-08-12T10:00:00Z"}
            """;
        var handler = new RecordingHandler(
            Json(HttpStatusCode.OK, $$"""{"items":[{{exceptionJson}}],"nextCursor":null}"""),
            Json(HttpStatusCode.Created, exceptionJson),
            JsonWithEntityTag(HttpStatusCode.OK, exceptionJson.Replace("\"version\":1", "\"version\":2"), "\"employee-exception:exception-1:v2\""),
            Json(HttpStatusCode.OK, $$"""{"resourceId":"resource-1","isActive":true,"assignedCalendarId":"calendar-1","timeZoneId":"UTC","windows":[{"startsAt":"2026-08-18T06:00:00Z","endsAt":"2026-08-18T10:00:00Z"}],"exceptions":[{{exceptionJson}}]}"""),
            new HttpResponseMessage(HttpStatusCode.NoContent));
        using var api = CreateClient(handler);

        var listed = await api.ListEmployeeExceptionsAsync("resource-1");
        var created = await api.CreateEmployeeExceptionAsync("resource-1",
            new("2026-08-18", "unavailable", false, "10:00", "12:00", "Appointment"),
            "windows-1", 33);
        var updated = await api.UpdateEmployeeExceptionAsync("resource-1", "exception-1",
            new("2026-08-18", "unavailable", false, "10:00", "12:00", "Medical appointment"),
            "\"employee-exception:exception-1:v1\"", "windows-1", 33);
        var availability = await api.GetEmployeeAvailabilityAsync("resource-1",
            DateTimeOffset.Parse("2026-08-18T00:00:00Z"), DateTimeOffset.Parse("2026-08-19T00:00:00Z"));
        await api.DeleteEmployeeExceptionAsync("resource-1", "exception-1", "windows-1", 33);

        Assert.Single(listed);
        Assert.Equal("unavailable", created.ExceptionType);
        Assert.Equal("\"employee-exception:exception-1:v2\"", updated.EntityTag);
        Assert.Single(availability.Windows);
        Assert.Equal("/api/v1/resources/resource-1/exceptions", handler.Requests[0].Path);
        Assert.Equal(HttpMethod.Post, handler.Requests[1].Method);
        Assert.Equal("33", handler.Requests[1].Generation);
        Assert.Equal("\"employee-exception:exception-1:v1\"", handler.Requests[2].IfMatch);
        Assert.StartsWith("/api/v1/resources/resource-1/availability?from=", handler.Requests[3].Path, StringComparison.Ordinal);
        Assert.Equal(HttpMethod.Delete, handler.Requests[4].Method);
    }

    private static PlannerApiClient CreateClient(HttpMessageHandler handler) => new(
        new HttpClient(handler)
        {
            BaseAddress = new Uri("http://planner-server:5080/")
        });

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage JsonWithEntityTag(
        HttpStatusCode status,
        string body,
        string entityTag)
    {
        var response = Json(status, body);
        response.Headers.TryAddWithoutValidation("ETag", entityTag);
        return response;
    }

    private static string CaseJson(string caseId) => $$"""
        {
          "caseId": "{{caseId}}",
          "partNumber": "PN-1",
          "name": "Part",
          "revision": "A",
          "customer": "Acme",
          "customerReference": null,
          "previewPath": null,
          "workingFolderPath": "C:\\Cases\\PN-1",
          "materialType": null,
          "materialSpecification": null,
          "rawMaterialForm": null,
          "rawMaterialDimensions": null,
          "currentSetupTimeSeconds": 10,
          "currentCycleTimePerPartSeconds": 20,
          "notes": null,
          "isActive": true,
          "version": 3,
          "createdAt": "2026-08-11T10:00:00Z",
          "updatedAt": "2026-08-11T10:00:00Z"
        }
        """;

    private static string MachineJson(string machineId) => $$"""
        {
          "machineId": "{{machineId}}",
          "number": "M-21",
          "name": "Mill 21",
          "processType": "mill",
          "axisType": "5-axis",
          "capabilities": ["probe", "high-speed"],
          "workingCalendarId": "calendar-day",
          "isActive": true,
          "displayEnabled": true,
          "picturePath": "C:\\MachinePictures\\M-21.jpg",
          "deviceId": null,
          "backlogCount": 0,
          "version": 1,
          "createdAt": "2026-08-11T10:00:00Z",
          "updatedAt": "2026-08-11T10:00:00Z"
        }
        """;

    private static string CalendarJson(string calendarId, string name) => $$"""
        {
          "workingCalendarId": "{{calendarId}}",
          "name": "{{name}}",
          "timeZoneId": "Asia/Jerusalem",
          "workdays": ["sunday", "monday", "tuesday", "wednesday", "thursday"],
          "shiftStartsAtLocal": "06:00",
          "shiftEndsAtLocal": "18:00",
          "scheduleKind": "weekly",
          "version": 1,
          "createdAt": "2026-08-11T10:00:00Z",
          "updatedAt": "2026-08-11T10:00:00Z"
        }
        """;

    private static string EditJson(string state, int generation, bool includePending = false) => $$"""
        {
          "state": "{{state}}",
          "generation": {{generation}},
          "holder": null,
          "pendingRequest": {{(includePending ? """
            {
              "requestId": "request-1",
              "requesterClientId": "windows-02",
              "requesterUserId": "Local Planner",
              "status": "pending",
              "requestedAt": "2026-08-11T10:00:00Z",
              "decisionDeadline": "2026-08-11T10:00:30Z"
            }
            """ : "null")}},
          "serverTime": "2026-08-11T10:00:00Z",
          "transferTimeoutSeconds": 30
        }
        """;

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> responses;

        internal RecordingHandler(params HttpResponseMessage[] responses)
        {
            this.responses = new Queue<HttpResponseMessage>(responses);
        }

        internal List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri!.PathAndQuery,
                ReadHeader(request, "X-Meimad-Client-Id"),
                ReadHeader(request, "X-Meimad-User-Id"),
                ReadHeader(request, "X-Meimad-Edit-Generation"),
                ReadHeader(request, "If-Match"),
                request.Content is null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(cancellationToken)));
            return responses.Dequeue();
        }

        private static string? ReadHeader(HttpRequestMessage request, string name) =>
            request.Headers.TryGetValues(name, out var values) ? values.Single() : null;
    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        string Path,
        string? ClientId,
        string? UserId,
        string? Generation,
        string? IfMatch,
        string Body);
}
