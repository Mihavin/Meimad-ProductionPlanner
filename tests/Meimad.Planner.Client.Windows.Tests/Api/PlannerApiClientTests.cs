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
            {"schemaVersion":1,"importToken":"import-1","workbookSha256":"abc123","expiresAt":"2099-08-20T10:00:00Z",
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
        Assert.Null(receipt.Created.BatchOperationIds);
        Assert.Null(receipt.PoolBatchOperationIds);
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
            {"schemaVersion":1,"importToken":"import-1","workbookSha256":"abc123","expiresAt":"2099-08-20T10:00:00Z",
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
            {"schemaVersion":1,"importToken":"import-1","workbookSha256":"abc123","expiresAt":"2099-08-20T10:00:00Z",
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
        Assert.Equal(10, viewModel.Mappings.Count(mapping => mapping.Scope == "planning"));
        Assert.Equal(13, viewModel.Mappings.Count(mapping => mapping.Scope == "open_orders"));
        Assert.Single(viewModel.MachineMappings);
        Assert.Equal(2, viewModel.Rows.Count);
        Assert.All(viewModel.Rows, row => Assert.Equal("Needs review", row.Status));
        Assert.False(viewModel.CanCommit);

        foreach (var mapping in viewModel.MachineMappings)
        {
            mapping.SelectedMachineCandidate = mapping.MachineChoices.Single();
        }
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
            {"schemaVersion":1,"importToken":"import-1","workbookSha256":"abc123","expiresAt":"2099-08-20T10:00:00Z",
             "workbook":{"fileName":"legacy.xlsx","sheets":[{"name":"Planning","rowCount":4,"columnCount":3}]},
             "suggestions":{"planningSheet":"Planning","openOrdersSheet":null,"planningColumns":[{"field":"partNumber","column":"A","header":"Part","confidence":1,"required":true},{"field":"quantity","column":"B","header":"Quantity","confidence":1,"required":true}],"openOrderColumns":[]},
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
        viewModel.ImportMachineAssignments = true;
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

        var selection = row.ToPlanningSelection("machine-1");
        var expectedRoute = Assert.Single(selection.ExpectedCaseRoute!);
        Assert.Equal("route-1", expectedRoute.CaseOperationId);
        Assert.Equal(1, expectedRoute.Version);
    }

    [Fact]
    public async Task Legacy_batch_creation_stays_blocked_when_the_preview_has_no_reviewable_case_route()
    {
        var owner = new LegacyExcelImportViewModel();
        var caseCandidate = new LegacyImportCaseCandidate("case-1", "PN-1", "Part", null, null, "Exact");
        var row = LegacyImportRowViewModel.Planning(new LegacyImportPlanningRow(
            "plan-1", "Planning", 5, "machine-a", 1,
            new LegacyImportPlanningValues(null, "PN-1", null, null, 1, null, null, null, null, null),
            [], new LegacyImportPlanningCandidates([caseCandidate], [], [], [], [])), [], owner);

        row.Decision = "create_batch_to_pool";
        row.SelectedCaseCandidate = caseCandidate;
        row.BatchNumber = "B-1";
        await row.AddAllocationAsync();
        row.Allocations.Single().Type = "stock";
        row.Allocations.Single().Quantity = "1";

        Assert.False(row.IsResolved);
        Assert.Empty(row.ToPlanningSelection(null).ExpectedCaseRoute!);
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
    public void Legacy_import_pattern_never_reuses_one_existing_batch_operation_for_another_row()
    {
        var owner = new LegacyExcelImportViewModel();
        var operation = new LegacyImportBatchOperationCandidate(
            "batch-operation-1", "batch-1", "B-104", "case-1", "PN-1", "route-1",
            10, "Mill", "not_started", null, 1, null, null, null);
        var candidates = new LegacyImportPlanningCandidates([], [], [], [], [operation]);
        var source = LegacyImportRowViewModel.Planning(new LegacyImportPlanningRow(
            "plan-1", "Planning", 5, "machine-a", 1,
            new LegacyImportPlanningValues(null, "PN-1", null, null, 1, null, null, null, null, null),
            [], candidates), [], owner);
        var target = LegacyImportRowViewModel.Planning(new LegacyImportPlanningRow(
            "plan-2", "Planning", 6, "machine-a", 2,
            new LegacyImportPlanningValues(null, "PN-1", null, null, 1, null, null, null, null, null),
            [], candidates), [], owner);
        source.SelectedExistingOperationCandidate = operation;

        Assert.True(source.IsResolved);
        Assert.False(target.ApplyExplicitPatternFrom(source));
        Assert.False(target.HasExplicitDecision);
        Assert.Null(target.SelectedExistingOperationCandidate);
    }

    [Fact]
    public void Legacy_import_wizard_exposes_only_mappings_for_selected_outcomes_and_requires_machine_suggestion_approval()
    {
        var clearMachine = new LegacyImportMachineCandidate(
            "machine-1", "M1", "Mill 1", "milling", "3-axis", [], [], 0.95m, "Exact section match");
        var runnerUp = new LegacyImportMachineCandidate(
            "machine-2", "M2", "Mill 2", "milling", "3-axis", [], [], 0.70m, "Possible match");
        var owner = new LegacyExcelImportViewModel();
        owner.Mappings.Add(LegacyImportMappingViewModel.Column("planning",
            new LegacyImportColumnSuggestion("partNumber", "A", "Part", 1), owner, "PN-1",
            [new LegacyImportSourceColumnChoice("A", "Part", "PN-1")]));
        owner.Mappings.Add(LegacyImportMappingViewModel.Column("open_orders",
            new LegacyImportColumnSuggestion("orderNumber", "B", "Order", 1), owner, "SO-1",
            [new LegacyImportSourceColumnChoice("B", "Order", "SO-1")]));
        owner.MachineMappings.Add(LegacyImportMappingViewModel.Machine(new LegacyImportMachineSection(
            "machine-a", "Planning", 1, "Mill A", 2, 3, [clearMachine, runnerUp]), owner));

        owner.ImportOrders = true;
        var orderMapping = Assert.Single(owner.IncludedMappings);
        Assert.Equal("open_orders", orderMapping.Scope);
        Assert.False(owner.ShowsMachineMappings);
        Assert.Empty(owner.IncludedMachineMappings);

        owner.ImportPoolBatches = true;
        Assert.Equal(2, owner.IncludedMappings.Count());
        Assert.False(owner.ShowsMachineMappings);

        owner.ImportMachineAssignments = true;
        Assert.True(owner.ShowsMachineMappings);
        var machineMapping = Assert.Single(owner.IncludedMachineMappings);
        Assert.True(machineMapping.HasClearMachineSuggestion);
        Assert.True(machineMapping.AcceptClearMachineSuggestion());
        Assert.Equal("machine-1", machineMapping.SelectedMachineId);
        Assert.Equal("Exact section match", machineMapping.SelectionReason);
    }

    [Fact]
    public void Legacy_import_automatic_machine_mapping_requires_exact_score_and_clear_lead()
    {
        var owner = new LegacyExcelImportViewModel();
        var exact = new LegacyImportMachineCandidate(
            "machine-1", "M1", "Mill 1", "milling", "3-axis", [], [], 0.95m, "Exact");
        var safelyLower = new LegacyImportMachineCandidate(
            "machine-2", "M2", "Mill 2", "milling", "3-axis", [], [], 0.80m, "Possible");
        var safe = LegacyImportMappingViewModel.Machine(new LegacyImportMachineSection(
            "safe", "Planning", 1, "M1", 2, 2, [exact, safelyLower]), owner);

        Assert.True(safe.HasSafeAutomaticMachineSuggestion);
        Assert.True(safe.AcceptSafeAutomaticMachineSuggestion());
        Assert.Equal("machine-1", safe.SelectedMachineId);

        var nameOnly = LegacyImportMappingViewModel.Machine(new LegacyImportMachineSection(
            "name", "Planning", 1, "Mill", 2, 2,
            [exact with { Score = 0.80m }, safelyLower with { Score = 0.40m }]), owner);
        Assert.False(nameOnly.HasSafeAutomaticMachineSuggestion);
        Assert.False(nameOnly.AcceptSafeAutomaticMachineSuggestion());

        var closeRunnerUp = LegacyImportMappingViewModel.Machine(new LegacyImportMachineSection(
            "ambiguous", "Planning", 1, "M1", 2, 2,
            [exact with { Score = 1.00m }, safelyLower with { Score = 0.90m }]), owner);
        Assert.False(closeRunnerUp.HasSafeAutomaticMachineSuggestion);
        Assert.False(closeRunnerUp.AcceptSafeAutomaticMachineSuggestion());
    }

    [Fact]
    public void Legacy_import_batch_number_template_uses_row_values_and_rejects_a_duplicate()
    {
        var owner = new LegacyExcelImportViewModel();
        var values = new LegacyImportPlanningValues(
            null, "PN-1", "REF-7", null, 1, null, null, null, null, null);
        var first = LegacyImportRowViewModel.Planning(new LegacyImportPlanningRow(
            "plan-1", "Planning", 7, "machine-a", 1, values, [],
            new LegacyImportPlanningCandidates([], [], [], [], [])), [], owner);
        var duplicate = LegacyImportRowViewModel.Planning(new LegacyImportPlanningRow(
            "plan-2", "Planning", 7, "machine-a", 2, values, [],
            new LegacyImportPlanningCandidates([], [], [], [], [])), [], owner);
        first.Decision = "create_batch_to_pool";
        duplicate.Decision = "create_batch_to_pool";
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Assert.True(first.TryApplyBatchNumberTemplate("IMP-{part}-{reference}-{row}", reserved));
        Assert.Equal("IMP-PN-1-REF-7-7", first.BatchNumber);
        Assert.False(duplicate.TryApplyBatchNumberTemplate("IMP-{part}-{reference}-{row}", reserved));
        Assert.True(string.IsNullOrWhiteSpace(duplicate.BatchNumber));
    }

    [Fact]
    public void Order_driven_batch_uses_source_batch_number_and_related_order_allocations()
    {
        var owner = new LegacyExcelImportViewModel();
        var caseCandidate = new LegacyImportCaseCandidate("case-1", "PN-X", "Part X", "A", "Customer", "exact");
        var route = new LegacyImportCaseOperationCandidate("route-1", "case-1", 10, "Machine", null, 0, 10, 2);
        var existingOrder = new LegacyImportOrderCandidate("order-1", "ORD-1", 16, "2026-09-10", "exact");
        var row = LegacyImportRowViewModel.Planning(new LegacyImportPlanningRow(
            "batch-row", "Orders", 2, "pool:pn-x", 1,
            new LegacyImportPlanningValues("Customer", "PN-X", "WO-7", null, 12, null, null, null, null, null),
            [],
            new LegacyImportPlanningCandidates([caseCandidate], [existingOrder], [], [route], []),
            [
                new LegacyImportRelatedOrder("order-row-1", "ORD-1", 7, "order-1"),
                new LegacyImportRelatedOrder("order-row-2", "ORD-2", 5, null)
            ]), [], owner);

        row.PreparePlanningAutomatically("WO-7");

        Assert.Equal("create_batch_to_pool", row.Decision);
        Assert.Equal("WO-7", row.BatchNumber);
        Assert.Equal(2, row.Allocations.Count);
        Assert.Equal(12, row.Allocations.Sum(allocation => int.Parse(allocation.Quantity)));
        Assert.Equal("order-1", row.Allocations[0].SelectedOrderCandidate?.OrderId);
        Assert.Equal("order-row-2", row.Allocations[1].OrderSourceRowKey);
        Assert.DoesNotContain(row.Allocations, allocation => allocation.Type == "stock");
    }

    [Fact]
    public void Case_stage_maps_only_requested_case_fields_and_generates_system_working_folder()
    {
        var owner = new LegacyExcelImportViewModel();
        var row = LegacyImportRowViewModel.OpenOrder(new LegacyImportOpenOrderRow(
            "order-row", "Orders", 2, 1,
            new LegacyImportOpenOrderValues(
                "PN-X", "ORD-1", null, "Customer X", "2026-09-10", "A", 7,
                "source notes", null, "source reference", 16, "Part X", null, "#", "WO-7"),
            [], new LegacyImportOpenOrderCandidates([], [])), [], owner);

        row.PrepareNewCaseAutomatically(@"C:\Import\_MeimadPlanner\Cases\PN-X");

        Assert.Equal("create_case", row.Decision);
        Assert.Equal("PN-X", row.NewCasePartNumber);
        Assert.Equal("Part X", row.NewCaseName);
        Assert.Equal("A", row.NewCaseRevision);
        Assert.Equal("Customer X", row.NewCaseCustomer);
        Assert.Equal(string.Empty, row.NewCaseCustomerReference);
        Assert.Equal(string.Empty, row.NewCaseNotes);
        Assert.Equal(@"C:\Import\_MeimadPlanner\Cases\PN-X", row.NewCaseWorkingFolderPath);
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

        // Switching an existing-operation choice into the unassigned-pool action
        // must not leak stale assignment-only fields into the atomic request.
        row.Decision = "create_batch_to_pool";
        row.RouteOperation = "route-stale";
        row.MachineId = "machine-stale";
        var poolSelection = row.ToPlanningSelection(incompatibleMachine.MachineId);
        Assert.Null(poolSelection.BatchOperationId);
        Assert.Null(poolSelection.CaseOperationId);
        Assert.Null(poolSelection.MachineId);
        Assert.Null(poolSelection.CompatibilityOverride);
    }

    [Fact]
    public async Task Legacy_import_column_mapping_keeps_the_target_field_stable_and_commits_the_selected_source_column()
    {
        const string previewJson = """
            {"schemaVersion":1,"importToken":"import-1","workbookSha256":"abc123","expiresAt":"2099-08-20T10:00:00Z",
             "workbook":{"fileName":"legacy.xlsx","sheets":[{"name":"Planning","rowCount":2,"columnCount":3},{"name":"Orders","rowCount":2,"columnCount":3}]},
             "suggestions":{"planningSheet":"Planning","openOrdersSheet":"Orders","planningColumns":[{"field":"partNumber","column":"A","header":"Part","confidence":1},{"field":"quantity","column":"B","header":"Quantity","confidence":1}],"openOrderColumns":[{"field":"partNumber","column":"A","header":"Part","confidence":1}]},
             "machineSections":[],"rows":[{"rowKey":"plan-1","sheetName":"Planning","rowNumber":2,"sectionKey":"none","sourceOrder":1,"values":{"partNumber":"PN-1","quantity":1},"provenance":[],"candidates":{"cases":[],"orders":[],"batches":[],"caseOperations":[],"batchOperations":[]}}],"openOrderRows":[{"rowKey":"open-1","sheetName":"Orders","rowNumber":2,"sourceOrder":1,"values":{"partNumber":"PN-2","orderNumber":"SO-2","customer":"Acme","outstandingQuantity":1},"provenance":[],"candidates":{"cases":[],"orders":[]}}],"issues":[]}
            """;
        const string receiptJson = """
            {"schemaVersion":1,"workbookSha256":"abc123","commitId":"commit-1","replayed":false,
             "created":{"caseIds":[],"orderIds":[],"batchIds":[],"assignmentIds":[]},
             "unchanged":{"caseIds":[],"orderIds":[],"batchIds":[],"assignmentIds":[]},"machineBacklogs":[]}
            """;
        var correctedPreviewJson = previewJson.Replace(
            "\"column\":\"A\"",
            "\"column\":\"C\"",
            StringComparison.Ordinal);
        var handler = new RecordingHandler(
            Json(HttpStatusCode.OK, previewJson),
            Json(HttpStatusCode.OK, correctedPreviewJson),
            Json(HttpStatusCode.OK, receiptJson));
        using var api = CreateClient(handler);
        var viewModel = new LegacyExcelImportViewModel(_ => new MemoryStream([0x50, 0x4B]), _ => true);
        viewModel.AttachSession(api, "windows-1", new EditModeStatus(
            ClientEditState.Editor, 9, null, null, DateTimeOffset.UtcNow, 30));
        viewModel.SetWorkbookSelection("legacy.xlsx");
        await viewModel.PreviewAsync();
        viewModel.ImportOrders = true;
        viewModel.ImportPoolBatches = true;

        var mapping = viewModel.Mappings.Single(candidate =>
            candidate.Scope == "planning" && candidate.TargetField == "partNumber");
        Assert.Equal("partNumber", mapping.TargetField);
        Assert.Equal(["A", "B", "C"], mapping.ColumnChoices);
        mapping.SourceColumn = "D";
        Assert.False(mapping.IsResolved);
        mapping.SourceColumn = "C";
        Assert.True(viewModel.HasPendingPreviewCorrections);
        Assert.False(viewModel.CanCommit);
        await viewModel.PreviewAsync();
        mapping = viewModel.Mappings.Single(candidate =>
            candidate.Scope == "planning" && candidate.TargetField == "partNumber");
        Assert.Equal("C", mapping.SourceColumn);
        Assert.False(viewModel.HasPendingPreviewCorrections);
        viewModel.Rows.Single(row => row.Kind == "planning").IsSkipped = true;
        var openOrder = viewModel.Rows.Single(row => row.Kind == "open_orders");
        openOrder.Decision = "create_case";
        openOrder.NewCasePartNumber = "PN-2";
        openOrder.NewCaseName = "Imported part";
        openOrder.NewCaseWorkingFolderPath = "C:\\Cases\\PN-2";
        Assert.True(viewModel.CanCommit);
        await viewModel.CommitAsync();

        Assert.Contains("name=planningSheet", handler.Requests[1].Body, StringComparison.Ordinal);
        Assert.Contains("name=columnMappings", handler.Requests[1].Body, StringComparison.Ordinal);
        Assert.Contains("\"field\":\"partNumber\",\"column\":\"C\"", handler.Requests[1].Body, StringComparison.Ordinal);
        Assert.Contains("\"field\":\"partNumber\",\"column\":\"C\"", handler.Requests[2].Body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"field\":\"C\"", handler.Requests[2].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Legacy_import_sheet_or_column_correction_requires_a_fresh_server_preview()
    {
        const string initialPreview = """
            {"schemaVersion":1,"importToken":"import-1","workbookSha256":"abc123","expiresAt":"2099-08-20T10:00:00Z",
             "workbook":{"fileName":"legacy.xlsx","sheets":[{"name":"Planning","rowCount":2,"columnCount":3},{"name":"Alternative","rowCount":3,"columnCount":3},{"name":"Orders","rowCount":2,"columnCount":4}]},
             "suggestions":{"planningSheet":"Planning","openOrdersSheet":"Orders","planningColumns":[{"field":"partNumber","column":"A","header":"Part","confidence":1,"required":true},{"field":"notes","column":"B","header":"Notes","confidence":0.8,"required":false}],"openOrderColumns":[]},
             "machineSections":[],"rows":[{"rowKey":"Planning!2","sheetName":"Planning","rowNumber":2,"sectionKey":"none","sourceOrder":1,"values":{"partNumber":"PN-OLD","quantity":1},"provenance":[],"candidates":{"cases":[],"orders":[],"batches":[],"caseOperations":[],"batchOperations":[]}}],"openOrderRows":[],"issues":[]}
            """;
        const string correctedPreview = """
            {"schemaVersion":1,"importToken":"import-2","workbookSha256":"abc123","expiresAt":"2099-08-20T10:05:00Z",
             "workbook":{"fileName":"legacy.xlsx","sheets":[{"name":"Planning","rowCount":2,"columnCount":3},{"name":"Alternative","rowCount":3,"columnCount":3},{"name":"Orders","rowCount":2,"columnCount":4}]},
             "suggestions":{"planningSheet":"Alternative","openOrdersSheet":"Orders","planningColumns":[{"field":"partNumber","column":"C","header":"Part","confidence":1,"required":true}],"openOrderColumns":[]},
             "machineSections":[],"rows":[{"rowKey":"Alternative!3","sheetName":"Alternative","rowNumber":3,"sectionKey":"none","sourceOrder":1,"values":{"partNumber":"PN-NEW","quantity":1},"provenance":[],"candidates":{"cases":[],"orders":[],"batches":[],"caseOperations":[],"batchOperations":[]}}],"openOrderRows":[],"issues":[]}
            """;
        var handler = new RecordingHandler(
            Json(HttpStatusCode.OK, initialPreview),
            Json(HttpStatusCode.OK, correctedPreview));
        using var api = CreateClient(handler);
        var viewModel = new LegacyExcelImportViewModel(_ => new MemoryStream([0x50, 0x4B]), _ => true);
        viewModel.AttachSession(api, "windows-1", new EditModeStatus(
            ClientEditState.Editor, 9, null, null, DateTimeOffset.UtcNow, 30));
        viewModel.SetWorkbookSelection("legacy.xlsx");

        await viewModel.PreviewAsync();

        Assert.Equal(["Planning", "Alternative", "Orders"], viewModel.SheetChoices);
        Assert.Equal(3, viewModel.DetectedSheets.Count);
        var mappings = viewModel.Mappings
            .Where(mapping => mapping.Scope == "planning")
            .ToDictionary(mapping => mapping.TargetField);
        Assert.True(mappings["partNumber"].IsRequired);
        Assert.False(mappings["notes"].IsRequired);
        mappings["notes"].SourceColumn = string.Empty;
        viewModel.SourceSheetName = "Alternative";
        mappings["partNumber"].SourceColumn = "C";

        Assert.True(viewModel.HasPendingPreviewCorrections);
        Assert.False(viewModel.CanCommit);
        Assert.Contains("refresh", viewModel.PreviewCorrectionStatus, StringComparison.OrdinalIgnoreCase);

        await viewModel.PreviewAsync();

        Assert.False(viewModel.HasPendingPreviewCorrections);
        Assert.Equal("Alternative", viewModel.SourceSheetName);
        Assert.True(string.IsNullOrWhiteSpace(viewModel.Mappings.Single(mapping =>
            mapping.Scope == "planning" && mapping.TargetField == "notes").SourceColumn));
        Assert.Equal("PN-NEW", Assert.Single(viewModel.Rows).SourcePartNumber);
        Assert.Contains("Alternative", handler.Requests[1].Body, StringComparison.Ordinal);
        Assert.Contains("\"field\":\"partNumber\",\"column\":\"C\"", handler.Requests[1].Body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"field\":\"notes\"", handler.Requests[1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Legacy_import_sheet_change_without_manual_mapping_asks_server_to_auto_map_the_new_sheet()
    {
        const string initialPreview = """
            {"schemaVersion":1,"importToken":"import-1","workbookSha256":"abc123","expiresAt":"2099-08-20T10:00:00Z",
             "workbook":{"fileName":"legacy.xlsx","sheets":[{"name":"Planning","rowCount":2,"columnCount":2},{"name":"Alternative","rowCount":2,"columnCount":2}]},
             "suggestions":{"planningSheet":"Planning","openOrdersSheet":null,"planningColumns":[{"field":"partNumber","column":"A","header":"Part","confidence":1,"required":true}],"openOrderColumns":[]},
             "machineSections":[],"rows":[],"openOrderRows":[],"issues":[]}
            """;
        const string remappedPreview = """
            {"schemaVersion":1,"importToken":"import-2","workbookSha256":"abc123","expiresAt":"2099-08-20T10:05:00Z",
             "workbook":{"fileName":"legacy.xlsx","sheets":[{"name":"Planning","rowCount":2,"columnCount":2},{"name":"Alternative","rowCount":2,"columnCount":2}]},
             "suggestions":{"planningSheet":"Alternative","openOrdersSheet":null,"planningColumns":[{"field":"partNumber","column":"B","header":"Item Number","confidence":0.98,"required":true}],"openOrderColumns":[]},
             "machineSections":[],"rows":[],"openOrderRows":[],"issues":[]}
            """;
        var handler = new RecordingHandler(
            Json(HttpStatusCode.OK, initialPreview),
            Json(HttpStatusCode.OK, remappedPreview));
        using var api = CreateClient(handler);
        var viewModel = new LegacyExcelImportViewModel(_ => new MemoryStream([0x50, 0x4B]), _ => true);
        viewModel.AttachSession(api, "windows-1", new EditModeStatus(
            ClientEditState.Editor, 9, null, null, DateTimeOffset.UtcNow, 30));
        viewModel.SetWorkbookSelection("legacy.xlsx");
        await viewModel.PreviewAsync();

        viewModel.SourceSheetName = "Alternative";
        await viewModel.PreviewAsync();

        Assert.Equal("B", viewModel.Mappings.Single(mapping =>
            mapping.Scope == "planning" && mapping.TargetField == "partNumber").SourceColumn);
        Assert.Contains("Alternative", handler.Requests[1].Body, StringComparison.Ordinal);
        Assert.DoesNotContain("name=columnMappings", handler.Requests[1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Legacy_import_zero_suggestions_still_exposes_all_targets_and_described_source_columns()
    {
        const string initialPreview = """
            {"schemaVersion":1,"importToken":"import-1","workbookSha256":"abc123","expiresAt":"2099-08-20T10:00:00Z",
             "workbook":{"fileName":"legacy.xlsx","sheets":[{"name":"Planning","rowCount":3,"columnCount":3,"columns":[{"column":"A","header":"Legacy Item","sample":"PN-1001"},{"column":"B","header":"Legacy Units","sample":"12"},{"column":"C","header":"Mystery note","sample":"urgent"}]}]},
             "suggestions":{"planningSheet":"Planning","openOrdersSheet":null,"planningColumns":[],"openOrderColumns":[]},
             "machineSections":[],"rows":[],"openOrderRows":[],"issues":[{"severity":"blocking","code":"required_column_mapping_missing","message":"Part mapping required","sheetName":null,"rowNumber":null,"field":"partNumber","sectionKey":null,"scope":"planning"}]}
            """;
        const string correctedPreview = """
            {"schemaVersion":1,"importToken":"import-2","workbookSha256":"abc123","expiresAt":"2099-08-20T10:05:00Z",
             "workbook":{"fileName":"legacy.xlsx","sheets":[{"name":"Planning","rowCount":3,"columnCount":3,"columns":[{"column":"A","header":"Legacy Item","sample":"PN-1001"},{"column":"B","header":"Legacy Units","sample":"12"},{"column":"C","header":"Mystery note","sample":"urgent"}]}]},
             "suggestions":{"planningSheet":"Planning","openOrdersSheet":null,"planningColumns":[{"field":"partNumber","column":"A","header":"Legacy Item","confidence":1,"required":true},{"field":"quantity","column":"B","header":"Legacy Units","confidence":1,"required":true}],"openOrderColumns":[]},
             "machineSections":[],"rows":[{"rowKey":"plan-1","sheetName":"Planning","rowNumber":2,"sectionKey":"none","sourceOrder":1,"values":{"partNumber":"PN-1001","quantity":12},"provenance":[],"candidates":{"cases":[],"orders":[],"batches":[],"caseOperations":[],"batchOperations":[]}}],"openOrderRows":[],"issues":[]}
            """;
        var handler = new RecordingHandler(Json(HttpStatusCode.OK, initialPreview), Json(HttpStatusCode.OK, correctedPreview));
        using var api = CreateClient(handler);
        var viewModel = new LegacyExcelImportViewModel(_ => new MemoryStream([0x50, 0x4B]), _ => true);
        viewModel.AttachSession(api, "windows-1", new EditModeStatus(
            ClientEditState.Editor, 9, null, null, DateTimeOffset.UtcNow, 30));
        viewModel.SetWorkbookSelection("legacy.xlsx");

        await viewModel.PreviewAsync();
        viewModel.ImportPoolBatches = true;

        var mappings = viewModel.IncludedMappings.ToDictionary(mapping => mapping.TargetField);
        Assert.Equal(10, mappings.Count);
        var part = mappings["partNumber"];
        var quantity = mappings["quantity"];
        Assert.True(part.IsRequired);
        Assert.True(quantity.IsRequired);
        Assert.False(part.IsResolved);
        Assert.Contains(part.ColumnOptions, option => option.DisplayName.Contains(
            "A - Legacy Item - PN-1001", StringComparison.Ordinal));

        part.SourceColumn = "A";
        quantity.SourceColumn = "B";
        Assert.Equal("Legacy Item", part.SourceHeader);
        Assert.Equal("PN-1001", part.SampleValue);
        Assert.True(viewModel.HasPendingPreviewCorrections);

        await viewModel.PreviewAsync();

        Assert.False(viewModel.HasPendingPreviewCorrections);
        Assert.Single(viewModel.Rows);
        Assert.Contains("\"field\":\"partNumber\",\"column\":\"A\"", handler.Requests[1].Body, StringComparison.Ordinal);
        Assert.Contains("\"field\":\"quantity\",\"column\":\"B\"", handler.Requests[1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Legacy_import_failed_refresh_invalidates_the_old_commit_gate_until_a_successful_preview()
    {
        const string preview = """
            {"schemaVersion":1,"importToken":"import-1","workbookSha256":"abc123","expiresAt":"2099-08-20T10:00:00Z",
             "workbook":{"fileName":"legacy.xlsx","sheets":[{"name":"Orders","rowCount":2,"columnCount":2,"columns":[{"column":"A","header":"Part","sample":"PN-2"}]}]},
             "suggestions":{"planningSheet":null,"openOrdersSheet":"Orders","planningColumns":[],"openOrderColumns":[{"field":"partNumber","column":"A","header":"Part","confidence":1,"required":true}]},
             "machineSections":[],"rows":[],"openOrderRows":[{"rowKey":"open-1","sheetName":"Orders","rowNumber":2,"sourceOrder":1,"values":{"partNumber":"PN-2"},"provenance":[],"candidates":{"cases":[],"orders":[]}}],"issues":[]}
            """;
        var refreshed = preview.Replace("import-1", "import-2", StringComparison.Ordinal);
        var handler = new RecordingHandler(
            Json(HttpStatusCode.OK, preview),
            Json(HttpStatusCode.UnprocessableEntity,
                """{"error":{"code":"invalid_workbook","message":"Refresh failed","correlationId":"corr-1","details":[]}}"""),
            Json(HttpStatusCode.OK, refreshed));
        using var api = CreateClient(handler);
        var viewModel = new LegacyExcelImportViewModel(_ => new MemoryStream([0x50, 0x4B]), _ => true);
        viewModel.AttachSession(api, "windows-1", new EditModeStatus(
            ClientEditState.Editor, 9, null, null, DateTimeOffset.UtcNow, 30));
        viewModel.SetWorkbookSelection("legacy.xlsx");
        await viewModel.PreviewAsync();
        viewModel.ImportOrders = true;
        var row = Assert.Single(viewModel.Rows);
        row.Decision = "create_case";
        row.NewCasePartNumber = "PN-2";
        row.NewCaseName = "Part 2";
        row.NewCaseWorkingFolderPath = "C:\\Cases\\PN-2";
        Assert.True(viewModel.CanCommit);

        await viewModel.PreviewAsync();

        Assert.True(viewModel.HasPendingPreviewCorrections);
        Assert.False(viewModel.CanCommit);
        Assert.False(viewModel.CanGoNext);

        await viewModel.PreviewAsync();

        Assert.False(viewModel.HasPendingPreviewCorrections);
        Assert.True(viewModel.CanGoNext);
    }

    [Fact]
    public async Task Legacy_import_preview_summary_reports_validation_matches_decisions_and_unknown_machines()
    {
        const string previewJson = """
            {"schemaVersion":1,"importToken":"import-1","workbookSha256":"abc123","expiresAt":"2099-08-20T10:00:00Z",
             "workbook":{"fileName":"legacy.xlsx","sheets":[{"name":"Planning","rowCount":2,"columnCount":3},{"name":"Orders","rowCount":2,"columnCount":4}]},
             "suggestions":{"planningSheet":"Planning","openOrdersSheet":"Orders","planningColumns":[],"openOrderColumns":[{"field":"partNumber","column":"A","header":"Part","confidence":1,"required":true}]},
             "machineSections":[{"sectionKey":"machine-a","sheetName":"Planning","headerRow":1,"sourceLabel":"Unknown cell","firstDataRow":2,"lastDataRow":2,"candidates":[{"machineId":"machine-1","number":"M1","name":"Mill","processType":"mill","axisType":"3-axis","capabilities":[],"machineTypeCapabilities":[],"score":0,"reason":"manual_choice"}]}],
             "rows":[{"rowKey":"plan-1","sheetName":"Planning","rowNumber":2,"sectionKey":"machine-a","sourceOrder":1,"values":{"partNumber":"PN-1","quantity":5},"provenance":[],"candidates":{"cases":[{"caseId":"case-1","partNumber":"PN-1","name":"Part","revision":null,"customer":null,"reason":"Exact"}],"orders":[],"batches":[],"caseOperations":[{"caseOperationId":"route-1","caseId":"case-1","operationNumber":10,"name":"Mill","requiredMachineType":null,"setupTimeSeconds":1,"cycleTimePerPartSeconds":2,"version":1},{"caseOperationId":"route-2","caseId":"case-1","operationNumber":20,"name":"Inspect","requiredMachineType":null,"setupTimeSeconds":0,"cycleTimePerPartSeconds":1,"version":1}],"batchOperations":[]}}],
             "openOrderRows":[{"rowKey":"order-1","sheetName":"Orders","rowNumber":2,"sourceOrder":1,"values":{"partNumber":"PN-1","orderNumber":"SO-1","deliveryDate":"2026-09-01","outstandingQuantity":5},"provenance":[],"candidates":{"cases":[{"caseId":"case-1","partNumber":"PN-1","name":"Part","revision":null,"customer":null,"reason":"Exact"}],"orders":[{"orderId":"existing-order","orderNumber":"SO-OLD","quantity":5,"workFinishDate":"2026-09-01","reason":"Part match"}]} }],
             "issues":[{"severity":"warning","code":"duplicate_order_candidate","message":"Existing Order candidate","sheetName":"Orders","rowNumber":2,"field":"orderNumber","sectionKey":null}]}
            """;
        var handler = new RecordingHandler(Json(HttpStatusCode.OK, previewJson));
        using var api = CreateClient(handler);
        var viewModel = new LegacyExcelImportViewModel(_ => new MemoryStream([0x50, 0x4B]), _ => true);
        viewModel.AttachSession(api, "windows-1", new EditModeStatus(
            ClientEditState.Editor, 9, null, null, DateTimeOffset.UtcNow, 30));
        viewModel.SetWorkbookSelection("legacy.xlsx");

        await viewModel.PreviewAsync();
        viewModel.ImportPoolBatches = true;
        viewModel.ImportOrders = true;
        var planning = viewModel.Rows.Single(row => row.Kind == "planning");
        planning.Decision = "create_batch_to_pool";
        planning.SelectedCaseCandidate = planning.CaseCandidates.Single();
        planning.BatchNumber = "B-1";
        await planning.AddAllocationAsync();
        planning.Allocations.Single().Type = "stock";
        planning.Allocations.Single().Quantity = "5";
        var order = viewModel.Rows.Single(row => row.Kind == "open_orders");
        order.Decision = "create_order";
        order.SelectedCaseCandidate = order.CaseCandidates.Single();

        Assert.Contains("Detected 2 rows", viewModel.PreviewSummary, StringComparison.Ordinal);
        Assert.Contains("1 warning row", viewModel.PreviewSummary, StringComparison.Ordinal);
        Assert.Contains("1 Order(s)", viewModel.PreviewSummary, StringComparison.Ordinal);
        Assert.Contains("1 Batch(es)", viewModel.PreviewSummary, StringComparison.Ordinal);
        Assert.Contains("2 route Batch Operation(s)", viewModel.PreviewSummary, StringComparison.Ordinal);
        Assert.Contains("1 unmatched Machine section(s)", viewModel.PreviewSummary, StringComparison.Ordinal);
        Assert.Contains("1 duplicate indicator(s)", viewModel.PreviewSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Legacy_import_orders_only_commit_omits_the_excluded_planning_sheet_and_its_blockers()
    {
        const string previewJson = """
            {"schemaVersion":1,"importToken":"import-1","workbookSha256":"abc123","expiresAt":"2099-08-20T10:00:00Z",
             "workbook":{"fileName":"legacy.xlsx","sheets":[{"name":"Planning","rowCount":2,"columnCount":3},{"name":"Orders","rowCount":2,"columnCount":3}]},
             "suggestions":{"planningSheet":"Planning","openOrdersSheet":"Orders","planningColumns":[],"openOrderColumns":[{"field":"partNumber","column":"A","header":"Part","confidence":1,"required":true}]},
             "machineSections":[],"rows":[],
             "openOrderRows":[{"rowKey":"open-1","sheetName":"Orders","rowNumber":2,"sourceOrder":1,"values":{"partNumber":"PN-2"},"provenance":[],"candidates":{"cases":[],"orders":[]}}],"issues":[{"severity":"blocking","code":"machine_sections_not_found","message":"No Machine sections","sheetName":"Planning","rowNumber":null,"field":null,"sectionKey":null,"scope":"planning"}]}
            """;
        const string receiptJson = """
            {"schemaVersion":1,"workbookSha256":"abc123","commitId":"commit-1","replayed":false,
             "created":{"caseIds":["case-1"],"orderIds":[],"batchIds":[],"assignmentIds":[]},
             "unchanged":{"caseIds":[],"orderIds":[],"batchIds":[],"assignmentIds":[]},"machineBacklogs":[]}
            """;
        var handler = new RecordingHandler(Json(HttpStatusCode.OK, previewJson), Json(HttpStatusCode.OK, receiptJson));
        using var api = CreateClient(handler);
        var viewModel = new LegacyExcelImportViewModel(_ => new MemoryStream([0x50, 0x4B]), _ => true);
        viewModel.AttachSession(api, "windows-1", new EditModeStatus(
            ClientEditState.Editor, 9, null, null, DateTimeOffset.UtcNow, 30));
        viewModel.SetWorkbookSelection("legacy.xlsx");
        await viewModel.PreviewAsync();
        viewModel.ImportOrders = true;
        var orderRow = Assert.Single(viewModel.Rows);
        orderRow.Decision = "create_case";
        orderRow.NewCasePartNumber = "PN-2";
        orderRow.NewCaseName = "Imported part";
        orderRow.NewCaseWorkingFolderPath = "C:\\Cases\\PN-2";

        Assert.True(viewModel.CanCommit);
        Assert.False(viewModel.CanCommitNow);
        Assert.False(viewModel.CommitCommand.CanExecute(null));
        while (viewModel.CanGoNext)
        {
            viewModel.NextStepCommand.Execute(null);
            await Task.Yield();
        }
        Assert.Equal(4, viewModel.WizardStep);
        Assert.True(viewModel.CanCommitNow);
        Assert.True(viewModel.CommitCommand.CanExecute(null));
        Assert.Equal("Create Case", Assert.Single(viewModel.ReviewRows).DecisionDisplayName);
        await viewModel.CommitAsync();

        Assert.Contains("\"planningSheet\":null", handler.Requests[1].Body, StringComparison.Ordinal);
        Assert.Contains("\"openOrdersSheet\":\"Orders\"", handler.Requests[1].Body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"scope\":\"planning\"", handler.Requests[1].Body, StringComparison.Ordinal);
        Assert.Contains("\"planningSelections\":[]", handler.Requests[1].Body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"action\":\"skip\"", handler.Requests[1].Body, StringComparison.Ordinal);
        Assert.True(viewModel.HasResultSummary);
        Assert.Contains("created 1 Case(s)", viewModel.ResultSummary, StringComparison.Ordinal);
        Assert.Contains("matched/unchanged", viewModel.ResultSummary, StringComparison.Ordinal);
        Assert.Contains("source row(s) skipped", viewModel.ResultSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Legacy_import_wizard_requires_explicit_outcomes_and_applies_only_safe_pool_patterns()
    {
        const string previewJson = """
            {"schemaVersion":1,"importToken":"import-1","workbookSha256":"abc123","expiresAt":"2099-08-20T10:00:00Z",
             "workbook":{"fileName":"legacy.xlsx","sheets":[{"name":"Planning","rowCount":3,"columnCount":3}]},
             "suggestions":{"planningSheet":"Planning","openOrdersSheet":null,"planningColumns":[{"field":"partNumber","column":"A","header":"Part","confidence":1,"required":true},{"field":"quantity","column":"B","header":"Quantity","confidence":1,"required":true}],"openOrderColumns":[]},
             "machineSections":[
               {"sectionKey":"machine-a","sheetName":"Planning","headerRow":1,"sourceLabel":"Mill A","firstDataRow":2,"lastDataRow":3,"candidates":[{"machineId":"machine-1","number":"M1","name":"Mill 1","score":0.95,"reason":"Exact"}]},
               {"sectionKey":"machine-b","sheetName":"Planning","headerRow":1,"sourceLabel":"Mill B","firstDataRow":4,"lastDataRow":4,"candidates":[{"machineId":"machine-2","number":"M2","name":"Mill 2","score":0.95,"reason":"Exact"}]}],
             "rows":[
               {"rowKey":"plan-1","sheetName":"Planning","rowNumber":2,"sectionKey":"machine-a","sourceOrder":1,"values":{"partNumber":"PN-1","quantity":5},"provenance":[],"candidates":{"cases":[{"caseId":"case-1","partNumber":"PN-1","name":"Part","revision":null,"customer":null,"reason":"Exact"}],"orders":[],"batches":[],"caseOperations":[{"caseOperationId":"route-1","caseId":"case-1","operationNumber":10,"name":"Mill","requiredMachineType":null,"setupTimeSeconds":1,"cycleTimePerPartSeconds":2,"version":3}],"batchOperations":[]}},
               {"rowKey":"plan-2","sheetName":"Planning","rowNumber":3,"sectionKey":"machine-a","sourceOrder":2,"values":{"partNumber":"PN-1","quantity":3},"provenance":[],"candidates":{"cases":[{"caseId":"case-1","partNumber":"PN-1","name":"Part","revision":null,"customer":null,"reason":"Exact"}],"orders":[],"batches":[],"caseOperations":[{"caseOperationId":"route-1","caseId":"case-1","operationNumber":10,"name":"Mill","requiredMachineType":null,"setupTimeSeconds":1,"cycleTimePerPartSeconds":2,"version":3}],"batchOperations":[]}},
               {"rowKey":"plan-3","sheetName":"Planning","rowNumber":4,"sectionKey":"machine-b","sourceOrder":3,"values":{"partNumber":"PN-1","quantity":7},"provenance":[],"candidates":{"cases":[{"caseId":"case-1","partNumber":"PN-1","name":"Part","revision":null,"customer":null,"reason":"Exact"}],"orders":[],"batches":[],"caseOperations":[{"caseOperationId":"route-1","caseId":"case-1","operationNumber":10,"name":"Mill","requiredMachineType":null,"setupTimeSeconds":1,"cycleTimePerPartSeconds":2,"version":3}],"batchOperations":[]}}],
             "openOrderRows":[],"issues":[]}
            """;
        const string receiptJson = """
            {"schemaVersion":1,"workbookSha256":"abc123","commitId":"commit-1","replayed":false,
             "created":{"caseIds":[],"orderIds":[],"batchIds":["batch-1","batch-2"],"batchOperationIds":["operation-1","operation-2"],"assignmentIds":[]},
             "unchanged":{"caseIds":[],"orderIds":[],"batchIds":[],"batchOperationIds":[],"assignmentIds":[]},
             "poolBatchOperationIds":["operation-1","operation-2"],"machineBacklogs":[]}
            """;
        var handler = new RecordingHandler(Json(HttpStatusCode.OK, previewJson), Json(HttpStatusCode.OK, receiptJson));
        using var api = CreateClient(handler);
        var viewModel = new LegacyExcelImportViewModel(_ => new MemoryStream([0x50, 0x4B]), _ => true);
        viewModel.AttachSession(api, "windows-1", new EditModeStatus(
            ClientEditState.Editor, 9, null, null, DateTimeOffset.UtcNow, 30));
        viewModel.SetWorkbookSelection("legacy.xlsx");

        await viewModel.PreviewAsync();

        Assert.False(viewModel.ImportOrders);
        Assert.False(viewModel.ImportPoolBatches);
        Assert.False(viewModel.ImportMachineAssignments);
        viewModel.NextStepCommand.Execute(null);
        Assert.Equal(1, viewModel.WizardStep);
        Assert.False(viewModel.CanGoNext);

        viewModel.ImportPoolBatches = true;
        Assert.True(viewModel.CanGoNext);
        var source = viewModel.Rows.Single(row => row.RowKey == "plan-1");
        source.Decision = "create_batch_to_pool";
        source.SelectedCaseCandidate = source.CaseCandidates.Single();
        source.BatchNumber = "B-1";
        await source.AddAllocationAsync();
        source.Allocations.Single().Type = "stock";
        source.Allocations.Single().Quantity = "5";
        Assert.True(source.IsResolved);

        viewModel.SelectedRow = source;
        Assert.True(viewModel.CanApplyPattern);
        viewModel.ApplySelectedPatternToSimilarCommand.Execute(null);

        var target = viewModel.Rows.Single(row => row.RowKey == "plan-2");
        Assert.Equal("create_batch_to_pool", target.Decision);
        Assert.Equal("case-1", target.SelectedCaseCandidate!.CaseId);
        Assert.Single(target.Allocations);
        Assert.Equal("stock", target.Allocations.Single().Type);
        Assert.Equal("3", target.Allocations.Single().Quantity);
        var allScopeTarget = viewModel.Rows.Single(row => row.RowKey == "plan-3");
        Assert.False(allScopeTarget.HasExplicitDecision);
        Assert.True(string.IsNullOrWhiteSpace(target.BatchNumber));
        Assert.False(target.IsResolved);
        Assert.Contains("need row-specific review", viewModel.PatternApplicationSummary, StringComparison.OrdinalIgnoreCase);

        viewModel.ApplySelectedPatternToAllCommand.Execute(null);
        Assert.Equal("create_batch_to_pool", allScopeTarget.Decision);
        Assert.Equal("7", Assert.Single(allScopeTarget.Allocations).Quantity);

        target.BatchNumber = "B-2";
        allScopeTarget.BatchNumber = "B-3";
        foreach (var mapping in viewModel.MachineMappings)
        {
            mapping.SelectedMachineCandidate = mapping.MachineChoices.First();
        }
        Assert.True(target.IsResolved);
        Assert.True(viewModel.CanCommit);
        await viewModel.CommitAsync();
        Assert.Contains("\"machineMappings\":[]", handler.Requests[1].Body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"machineId\":\"machine-1\"", handler.Requests[1].Body, StringComparison.Ordinal);
        Assert.Contains("\"expectedCaseRoute\":[{\"caseOperationId\":\"route-1\",\"version\":3}]", handler.Requests[1].Body, StringComparison.Ordinal);

        viewModel.ImportPoolBatches = false;
        Assert.False(source.IsResolved);
        Assert.False(viewModel.CanCommit);
    }

    [Fact]
    public async Task Legacy_import_automatic_draft_uses_only_exact_safe_candidates_and_requires_skip_confirmation()
    {
        const string previewJson = """
            {"schemaVersion":1,"importToken":"import-auto","workbookSha256":"abc12345feed9876","expiresAt":"2099-08-20T10:00:00Z",
             "workbook":{"fileName":"legacy.xlsx","sheets":[{"name":"Planning","rowCount":5,"columnCount":3},{"name":"Orders","rowCount":4,"columnCount":4}]},
             "suggestions":{"planningSheet":"Planning","openOrdersSheet":"Orders",
               "planningColumns":[{"field":"partNumber","column":"A","header":"Part","confidence":0.65,"required":true},{"field":"quantity","column":"B","header":"Quantity","confidence":0.65,"required":true}],
               "openOrderColumns":[{"field":"partNumber","column":"A","header":"Part","confidence":0.65,"required":true},{"field":"orderNumber","column":"B","header":"Order","confidence":0.65},{"field":"deliveryDate","column":"C","header":"Finish","confidence":0.65},{"field":"outstandingQuantity","column":"D","header":"Quantity","confidence":0.65}]},
             "machineSections":[{"sectionKey":"machine-a","sheetName":"Planning","headerRow":1,"sourceLabel":"Machine 1","firstDataRow":2,"lastDataRow":5,
               "candidates":[
                 {"machineId":"machine-1","number":"1","name":"Mill 1","processType":"milling","axisType":"3-axis","capabilities":[],"machineTypeCapabilities":[],"score":1.0,"reason":"machine_number_exact"},
                 {"machineId":"machine-2","number":"2","name":"Mill 2","processType":"milling","axisType":"3-axis","capabilities":[],"machineTypeCapabilities":[],"score":0.70,"reason":"manual_choice"}] }],
             "rows":[
               {"rowKey":"plan-assign","sheetName":"Planning","rowNumber":2,"sectionKey":"machine-a","sourceOrder":1,"values":{"partNumber":"PN-1","quantity":5},"provenance":[],"candidates":{"cases":[{"caseId":"case-1","partNumber":"PN-1","name":"Part 1","revision":null,"customer":null,"reason":"part_number_exact"}],"orders":[],"batches":[],"caseOperations":[{"caseOperationId":"route-1","caseId":"case-1","operationNumber":10,"name":"Mill","requiredMachineType":"milling","setupTimeSeconds":1,"cycleTimePerPartSeconds":2,"version":3}],"batchOperations":[]}},
               {"rowKey":"plan-pool","sheetName":"Planning","rowNumber":3,"sectionKey":"machine-a","sourceOrder":2,"values":{"partNumber":"PN-2","quantity":7},"provenance":[],"candidates":{"cases":[{"caseId":"case-2","partNumber":"PN-2","name":"Part 2","revision":null,"customer":null,"reason":"part_number_exact"}],"orders":[],"batches":[],"caseOperations":[{"caseOperationId":"route-2","caseId":"case-2","operationNumber":10,"name":"Mill","requiredMachineType":"milling","setupTimeSeconds":1,"cycleTimePerPartSeconds":2,"version":2}],"batchOperations":[]}},
               {"rowKey":"plan-blocked","sheetName":"Planning","rowNumber":4,"sectionKey":"machine-a","sourceOrder":3,"values":{"partNumber":"PN-3","quantity":null},"provenance":[],"candidates":{"cases":[],"orders":[],"batches":[],"caseOperations":[],"batchOperations":[]}},
               {"rowKey":"plan-duplicate","sheetName":"Planning","rowNumber":5,"sectionKey":"machine-a","sourceOrder":4,"values":{"partNumber":"PN-4","quantity":3},"provenance":[],"candidates":{"cases":[{"caseId":"case-4","partNumber":"PN-4","name":"Part 4","revision":null,"customer":null,"reason":"part_number_exact"}],"orders":[],"batches":[],"caseOperations":[{"caseOperationId":"route-4","caseId":"case-4","operationNumber":10,"name":"Mill","requiredMachineType":"milling","setupTimeSeconds":1,"cycleTimePerPartSeconds":2,"version":1}],"batchOperations":[]}}],
             "openOrderRows":[
               {"rowKey":"order-create","sheetName":"Orders","rowNumber":2,"sourceOrder":1,"values":{"partNumber":"PN-1","orderNumber":"SO-NEW","deliveryDate":"2026-08-19","outstandingQuantity":5},"provenance":[],"candidates":{"cases":[{"caseId":"case-1","partNumber":"PN-1","name":"Part 1","revision":null,"customer":null,"reason":"part_number_exact"}],"orders":[]}},
               {"rowKey":"order-existing","sheetName":"Orders","rowNumber":3,"sourceOrder":2,"values":{"partNumber":"PN-1","orderNumber":"SO-OLD","deliveryDate":"2026-08-19","outstandingQuantity":5},"provenance":[],"candidates":{"cases":[{"caseId":"case-1","partNumber":"PN-1","name":"Part 1","revision":null,"customer":null,"reason":"part_number_exact"}],"orders":[{"orderId":"order-1","orderNumber":"SO-OLD","quantity":5,"workFinishDate":"2026-08-19","reason":"order_number_and_case_exact"}]}},
               {"rowKey":"order-ambiguous","sheetName":"Orders","rowNumber":4,"sourceOrder":3,"values":{"partNumber":"PN-X","orderNumber":"SO-X","deliveryDate":"2026-08-19","outstandingQuantity":2},"provenance":[],"candidates":{"cases":[{"caseId":"case-x1","partNumber":"PN-X","name":"Part X1","revision":null,"customer":null,"reason":"part_number_exact"},{"caseId":"case-x2","partNumber":"PN-X","name":"Part X2","revision":null,"customer":null,"reason":"part_number_exact"}],"orders":[]}}],
             "issues":[
               {"severity":"warning","code":"machine_type_override_required","message":"Server compatibility review required.","sheetName":"Planning","rowNumber":3,"field":"machineId","sectionKey":"machine-a"},
               {"severity":"blocking","code":"quantity_required","message":"Quantity is required.","sheetName":"Planning","rowNumber":4,"field":"quantity","sectionKey":"machine-a"},
               {"severity":"warning","code":"duplicate_source_row","message":"Duplicate source row.","sheetName":"Planning","rowNumber":5,"sectionKey":"machine-a"}]}
            """;
        var handler = new RecordingHandler(
            Json(HttpStatusCode.OK, previewJson),
            Json(HttpStatusCode.OK, previewJson.Replace("import-auto", "import-auto-refresh", StringComparison.Ordinal)));
        using var api = CreateClient(handler);
        var viewModel = new LegacyExcelImportViewModel(
            _ => new MemoryStream([0x50, 0x4B]), _ => true);
        viewModel.AttachSession(api, "windows-1", new EditModeStatus(
            ClientEditState.Editor, 9, null, null, DateTimeOffset.UtcNow, 30));
        viewModel.SetWorkbookSelection("legacy.xlsx");

        await viewModel.PreviewAsync();
        await viewModel.PrepareAutomaticallyAsync();

        Assert.True(viewModel.AutomaticPrepared);
        Assert.True(viewModel.ImportOrders);
        Assert.True(viewModel.ImportPoolBatches);
        Assert.True(viewModel.ImportMachineAssignments);
        Assert.Equal("machine-1", Assert.Single(viewModel.MachineMappings).SelectedMachineId);

        var assigned = viewModel.Rows.Single(row => row.RowKey == "plan-assign");
        Assert.Equal("create_batch_and_assign", assigned.Decision);
        Assert.Equal("route-1", assigned.SelectedRouteOperationCandidate!.CaseOperationId);
        Assert.Equal("IMP-ABC12345-2", assigned.BatchNumber);
        Assert.Equal("stock", Assert.Single(assigned.Allocations).Type);
        Assert.Equal("5", Assert.Single(assigned.Allocations).Quantity);
        Assert.Null(assigned.SelectedExistingOperationCandidate);
        Assert.False(assigned.CompatibilityOverrideConfirmed);

        var pooled = viewModel.Rows.Single(row => row.RowKey == "plan-pool");
        Assert.Equal("create_batch_to_pool", pooled.Decision);
        Assert.Null(pooled.SelectedRouteOperationCandidate);
        Assert.False(pooled.CompatibilityOverrideConfirmed);

        Assert.Equal("create_order", viewModel.Rows.Single(row => row.RowKey == "order-create").Decision);
        Assert.Contains("existing Order", viewModel.Rows.Single(row => row.RowKey == "order-existing").AutomaticReason, StringComparison.Ordinal);
        Assert.Contains("more than one existing Case", viewModel.Rows.Single(row => row.RowKey == "order-ambiguous").AutomaticReason, StringComparison.Ordinal);
        Assert.Contains("blocking", viewModel.Rows.Single(row => row.RowKey == "plan-blocked").AutomaticReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("duplicate", viewModel.Rows.Single(row => row.RowKey == "plan-duplicate").AutomaticReason, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(3, viewModel.AutomaticReadyRows);
        Assert.Equal(4, viewModel.AutomaticSkippedRows);
        Assert.Equal(4, viewModel.AutomaticAttentionRows.Count);
        Assert.All(viewModel.AutomaticAttentionRows, row => Assert.False(string.IsNullOrWhiteSpace(row.AutomaticReason)));
        Assert.Contains("2 explicit stock allocation(s)", viewModel.AutomaticImportSummary, StringComparison.Ordinal);
        Assert.True(viewModel.RequiresAutomaticSkipConfirmation);
        Assert.False(viewModel.CanCommit);
        Assert.Equal(4, viewModel.WizardStep);

        viewModel.ConfirmAutomaticSkips = true;
        Assert.True(viewModel.CanCommit);
        assigned.BatchNumber = "MANUAL-BATCH";
        Assert.False(viewModel.ConfirmAutomaticSkips);
        Assert.False(viewModel.CanCommit);

        viewModel.ImportPoolBatches = false;
        await viewModel.PrepareAutomaticallyAsync();
        Assert.False(viewModel.ImportPoolBatches);
        Assert.Equal("MANUAL-BATCH", assigned.BatchNumber);

        await viewModel.PreviewAsync();
        Assert.False(viewModel.AutomaticPrepared);
        Assert.False(viewModel.ConfirmAutomaticSkips);
        Assert.Empty(viewModel.AutomaticAttentionRows);
    }

    [Fact]
    public async Task Fixed_case_order_import_excludes_every_planning_and_machine_mutation()
    {
        const string previewJson = """
            {"schemaVersion":1,"importToken":"fixed-token","workbookSha256":"fixed-hash","expiresAt":"2099-08-20T10:00:00Z",
             "workbook":{"fileName":"working-plan.xlsx","sheets":[{"name":"Sheet1","rowCount":4,"columnCount":15}]},
             "suggestions":{"planningSheet":null,"openOrdersSheet":"Sheet1","planningColumns":[],"openOrderColumns":[
               {"field":"partNumber","column":"A","header":"Part","confidence":1,"required":true},
               {"field":"orderNumber","column":"B","header":"Order","confidence":1},
               {"field":"customer","column":"D","header":"Customer","confidence":1},
               {"field":"deliveryDate","column":"E","header":"Finish","confidence":1},
               {"field":"revision","column":"F","header":"REV","confidence":1},
               {"field":"orderedQuantity","column":"L","header":"Quantity","confidence":1},
               {"field":"productionInstruction","column":"N","header":"Active","confidence":1},
               {"field":"itemName","column":"O","header":"Name","confidence":1}]},
             "machineSections":[],"rows":[],
             "openOrderRows":[
               {"rowKey":"Sheet1!2","sheetName":"Sheet1","rowNumber":2,"sourceOrder":1,
                "values":{"partNumber":"PN-1","orderNumber":"ORD-1","customer":"Customer","deliveryDate":"2026-09-10","revision":"A","orderedQuantity":5,"itemName":"Part One","productionInstruction":"Y"},
                "provenance":[],"candidates":{"cases":[],"orders":[]}},
               {"rowKey":"Sheet1!3","sheetName":"Sheet1","rowNumber":3,"sourceOrder":2,
                "values":{"partNumber":"PN-2","orderNumber":"ORD-2","deliveryDate":"2026-09-11","orderedQuantity":3,"itemName":"Part Two","productionInstruction":"Y"},
                "provenance":[],"candidates":{"cases":[{"caseId":"case-2","partNumber":"PN-2","name":"Part Two","revision":null,"customer":null,"reason":"part_number_exact"}],"orders":[{"orderId":"order-2","orderNumber":"ORD-2","quantity":3,"workFinishDate":"2026-09-11","reason":"order_number_and_case_exact"}]}},
               {"rowKey":"Sheet1!4","sheetName":"Sheet1","rowNumber":4,"sourceOrder":3,
                "values":{"partNumber":"PN-3","orderNumber":"ORD-3","deliveryDate":"2026-09-12","orderedQuantity":null,"itemName":"Part Three","productionInstruction":"Y"},
                "provenance":[],"candidates":{"cases":[],"orders":[]}}],
             "issues":[{"severity":"blocking","code":"quantity_required","message":"Quantity must be positive.","sheetName":"Sheet1","rowNumber":4,"field":"orderedQuantity","scope":"open_orders"}]}
            """;
        const string receiptJson = """
            {"schemaVersion":1,"workbookSha256":"fixed-hash","commitId":"fixed-commit","replayed":false,
             "created":{"caseIds":["case-1"],"orderIds":["order-1"],"batchIds":[],"assignmentIds":[]},
             "unchanged":{"caseIds":["case-2"],"orderIds":["order-2"],"batchIds":[],"assignmentIds":[]},
             "machineBacklogs":[]}
            """;
        var handler = new RecordingHandler(
            Json(HttpStatusCode.OK, previewJson),
            Json(HttpStatusCode.OK, previewJson),
            Json(HttpStatusCode.OK, receiptJson));
        using var api = CreateClient(handler);
        var viewModel = new LegacyExcelImportViewModel(
            _ => new MemoryStream([0x50, 0x4B]), _ => true);
        viewModel.AttachSession(api, "windows-1", new EditModeStatus(
            ClientEditState.Editor, 9, null, null, DateTimeOffset.UtcNow, 30));
        viewModel.SetWorkbookSelection("working-plan.xlsx");

        await viewModel.PreviewDefinedImportAsync();

        var fixedPreview = handler.Requests[1].Body;
        Assert.Contains("\"field\":\"partNumber\",\"column\":\"A\"", fixedPreview, StringComparison.Ordinal);
        Assert.Contains("\"field\":\"orderNumber\",\"column\":\"B\"", fixedPreview, StringComparison.Ordinal);
        Assert.Contains("\"field\":\"customer\",\"column\":\"D\"", fixedPreview, StringComparison.Ordinal);
        Assert.Contains("\"field\":\"deliveryDate\",\"column\":\"E\"", fixedPreview, StringComparison.Ordinal);
        Assert.Contains("\"field\":\"revision\",\"column\":\"F\"", fixedPreview, StringComparison.Ordinal);
        Assert.Contains("\"field\":\"orderedQuantity\",\"column\":\"L\"", fixedPreview, StringComparison.Ordinal);
        Assert.Contains("\"field\":\"productionInstruction\",\"column\":\"N\"", fixedPreview, StringComparison.Ordinal);
        Assert.Contains("\"field\":\"itemName\",\"column\":\"O\"", fixedPreview, StringComparison.Ordinal);
        Assert.DoesNotContain("batchNumber", fixedPreview, StringComparison.Ordinal);
        Assert.True(viewModel.ImportOrders);
        Assert.False(viewModel.ImportPoolBatches);
        Assert.False(viewModel.ImportMachineAssignments);
        Assert.Empty(viewModel.MachineMappings);
        Assert.Equal(3, viewModel.SimpleCaseOrderRows.Count);
        Assert.Equal("create_case", viewModel.SimpleCaseOrderRows[0].Decision);
        Assert.True(viewModel.SimpleCaseOrderRows[1].IsSkipped);
        Assert.True(viewModel.SimpleCaseOrderRows[2].IsSkipped);
        viewModel.ConfirmAutomaticSkips = false;
        Assert.False(viewModel.CanCommit);
        Assert.True(viewModel.CanImportCasesAndOrders);
        Assert.StartsWith("Ready.", viewModel.CaseOrderImportAvailabilityText, StringComparison.Ordinal);

        await viewModel.ImportCasesAndOrdersAsync();

        var commit = handler.Requests[2].Body;
        Assert.Contains("\"planningSheet\":null", commit, StringComparison.Ordinal);
        Assert.Contains("\"machineMappings\":[]", commit, StringComparison.Ordinal);
        Assert.Contains("\"planningSelections\":[]", commit, StringComparison.Ordinal);
        Assert.Contains("\"action\":\"create_case\"", commit, StringComparison.Ordinal);
        Assert.DoesNotContain("create_batch", commit, StringComparison.Ordinal);
        Assert.DoesNotContain("machineId", commit, StringComparison.Ordinal);
        Assert.Contains("Cases created: 1", viewModel.ResultSummary, StringComparison.Ordinal);
        Assert.Contains("Orders matched existing: 1", viewModel.ResultSummary, StringComparison.Ordinal);
        Assert.Contains("Rows with errors: 1", viewModel.ResultSummary, StringComparison.Ordinal);
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
    public async Task Haas_connection_tests_preserve_typed_diagnostics_returned_with_bad_gateway()
    {
        var handler = new RecordingHandler(
            Json(HttpStatusCode.BadGateway, """
                {
                  "succeeded": false,
                  "message": "MDC connection to 192.168.0.56:5051 was refused.",
                  "programNumber": null,
                  "machineStatus": null,
                  "parts": null,
                  "header": null
                }
                """),
            Json(HttpStatusCode.BadGateway, """
                {
                  "succeeded": false,
                  "message": "The configured Local Net Share is unavailable.",
                  "programNumber": null,
                  "machineStatus": null,
                  "parts": null,
                  "header": null
                }
                """));
        using var api = CreateClient(handler);

        var mdc = await api.TestHaasMdcAsync("machine/haas");
        var share = await api.TestHaasNetShareAsync("machine/haas");

        Assert.False(mdc.Succeeded);
        Assert.Equal("MDC connection to 192.168.0.56:5051 was refused.", mdc.Message);
        Assert.False(share.Succeeded);
        Assert.Equal("The configured Local Net Share is unavailable.", share.Message);
        Assert.Equal("/api/v1/machines/machine%2Fhaas/haas/test-mdc", handler.Requests[0].Path);
        Assert.Equal("/api/v1/machines/machine%2Fhaas/haas/test-net-share", handler.Requests[1].Path);
    }

    [Fact]
    public async Task Haas_MTConnect_connection_test_posts_to_dedicated_endpoint()
    {
        var handler = new RecordingHandler(Json(HttpStatusCode.OK, """
            {
              "succeeded": true,
              "message": "MTConnect agent connection succeeded.",
              "programNumber": "1500.CNC",
              "machineStatus": "STOPPED",
              "parts": 9300,
              "header": null
            }
            """));
        using var api = CreateClient(handler);

        var result = await api.TestHaasMtConnectAsync("machine/haas");

        Assert.True(result.Succeeded);
        Assert.Equal("1500.CNC", result.ProgramNumber);
        Assert.Equal("STOPPED", result.MachineStatus);
        Assert.Equal(9300, result.Parts);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("/api/v1/machines/machine%2Fhaas/haas/test-mtconnect", handler.Requests[0].Path);
    }

    [Fact]
    public async Task Haas_connection_update_serializes_selected_telemetry_provider()
    {
        var handler = new RecordingHandler(Json(HttpStatusCode.OK, """
            {
              "machineId": "machine-haas",
              "host": "192.168.0.56",
              "mdcPort": 5051,
              "mtConnectPort": 8082,
              "localNetShareEnabled": false,
              "localNetSharePath": null,
              "credentialsReference": null,
              "partCounterSource": "Q500",
              "pollingIntervalMs": 2000,
              "connectionTimeoutMs": 3000,
              "stableProgramPolls": 2,
              "headerLineLimit": 50,
              "headerByteLimit": 32768,
              "headerPartPatterns": ["PART"],
              "enabled": true,
              "version": 2,
              "updatedAt": "2026-08-23T12:00:00Z",
              "telemetryProvider": "MTCONNECT"
            }
            """));
        using var api = CreateClient(handler);
        var update = new HaasConnectionUpdate(
            "192.168.0.56", 5051, 8082, 8080, false, null, null,
            "Q500", 2000, 3000, 2, 50, 32768,
            ["PART"], true, 1, "MTCONNECT");

        var result = await api.UpdateHaasConnectionAsync(
            "machine/haas", update, "windows-1", 19);

        Assert.Equal("MTCONNECT", result.TelemetryProvider);
        Assert.Equal(HttpMethod.Put, handler.Requests[0].Method);
        Assert.Equal("/api/v1/machines/machine%2Fhaas/haas/connection", handler.Requests[0].Path);
        Assert.Equal("19", handler.Requests[0].Generation);
        Assert.Contains("\"telemetryProvider\":\"MTCONNECT\"", handler.Requests[0].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Haas_connection_test_still_surfaces_standard_API_error_envelopes()
    {
        var handler = new RecordingHandler(Json(HttpStatusCode.NotFound, """
            {
              "error": {
                "code": "haas_settings_not_found",
                "message": "Haas NGC is not configured for this Machine."
              }
            }
            """));
        using var api = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<PlannerApiException>(() =>
            api.TestHaasMdcAsync("machine-haas"));

        Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
        Assert.Equal("haas_settings_not_found", exception.Code);
        Assert.Equal("Haas NGC is not configured for this Machine.", exception.Message);
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

        var cases = await api.ListCasesAsync(new CaseQuery(
            "PN / 1",
            "Acme & Co",
            true,
            "closestOrderDeliveryDate"));
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
            "/api/v1/cases?search=PN%20%2F%201&customer=Acme%20%26%20Co&isActive=true&sort=closestOrderDeliveryDate",
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
    public async Task Batch_update_uses_patch_etag_edit_headers_and_balanced_allocations()
    {
        var handler = new RecordingHandler(Json(HttpStatusCode.OK, """
            {"batchId":"batch-1","caseId":"case-1","batchNumber":"B-2","status":"waiting","plannedQuantity":7,"routeRevision":null,"allocations":[{"allocationId":"a-1","allocationType":"stock","orderId":null,"quantity":7}],"batchOperationCount":2,"version":2,"createdAt":"2026-08-11T00:00:00Z","updatedAt":"2026-08-12T00:00:00Z"}
            """));
        using var api = CreateClient(handler);

        var updated = await api.UpdateBatchAsync(
            "batch-1",
            new ProductionBatchUpdate("B-2", 7, [new("stock", null, 7)]),
            "\"batch:batch-1:v1\"",
            "windows-01",
            31);

        Assert.Equal("B-2", updated.BatchNumber);
        Assert.Equal(7, Assert.Single(updated.Allocations!).Quantity);
        Assert.Equal(HttpMethod.Patch, handler.Requests[0].Method);
        Assert.Equal("/api/v1/batches/batch-1", handler.Requests[0].Path);
        Assert.Equal("\"batch:batch-1:v1\"", handler.Requests[0].IfMatch);
        Assert.Equal("31", handler.Requests[0].Generation);
        Assert.Contains("\"plannedQuantity\":7", handler.Requests[0].Body);
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

    [Fact]
    public async Task Cnc_verification_configuration_is_typed_write_only_and_edit_guarded()
    {
        const string responseJson = """
            {"machineId":"machine-1","dprintTransport":"HAAS_DPRNT_TCP","dprintPort":8080,"challengeProgramNumber":9001,"verifyProgramNumber":9002,"customGcodeAlias":605,"nonceVariable":10801,"responseVariable":10802,"verificationStateVariable":10803,"releaseTokenVariable":10804,"secretConfigured":true,"expectedMacroVersion":3,"responseCodeDigits":6,"verificationTimeoutSeconds":300,"enabled":false,"version":1,"updatedAt":"2026-08-26T12:00:00Z"}
            """;
        var handler = new RecordingHandler(
            Json(HttpStatusCode.OK, responseJson),
            Json(HttpStatusCode.OK, responseJson.Replace("\"version\":1", "\"version\":2")));
        using var api = CreateClient(handler);

        var current = await api.GetCncVerificationSettingsAsync("machine-1");
        var saved = await api.UpdateCncVerificationSettingsAsync("machine-1", new(
            "HAAS_DPRNT_TCP", 8080, 9001, 9002, 605, 10801, 10802, 10803, 10804,
            "a-machine-secret-value", 3, 6, 300, false, 1), "windows-1", 42);

        Assert.True(current.SecretConfigured);
        Assert.Equal(2, saved.Version);
        Assert.Equal("/api/v1/machines/machine-1/verification-configuration", handler.Requests[0].Path);
        Assert.Equal(HttpMethod.Put, handler.Requests[1].Method);
        Assert.Equal("42", handler.Requests[1].Generation);
        Assert.Contains("\"verificationSecret\":\"a-machine-secret-value\"", handler.Requests[1].Body, StringComparison.Ordinal);
        Assert.DoesNotContain("protectedSecret", handler.Requests[1].Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cnc_recovery_and_replacement_loader_use_typed_edit_guarded_contracts()
    {
        const string releaseJson = """
            {"offsetLoaderReleaseId":"ol-2","productionRunId":"run:1","machineId":"machine-1",
            "ncReleaseId":"nc-1","toolTableReleaseId":"tools-1","verificationReleaseToken":483920,
            "artifactHash":null,"createdAt":"2026-08-26T12:00:00Z","createdBy":"user-1",
            "metadataJson":"{}","isCurrent":true}
            """;
        const string invalidateJson = """
            {"action":"INVALIDATE_VERIFICATION","productionRunId":"run:1","machineId":"machine-1",
            "verificationSessionId":"session-1","offsetLoaderReleaseId":"ol-2","reason":"Fixture changed",
            "performedBy":"user-1","performedAt":"2026-08-26T12:01:00Z"}
            """;
        const string revokeJson = """
            {"action":"REVOKE_CURRENT_OFFSET_LOADER","productionRunId":"run:1","machineId":"machine-1",
            "verificationSessionId":null,"offsetLoaderReleaseId":"ol-2","reason":"Offsets invalid",
            "performedBy":"user-1","performedAt":"2026-08-26T12:02:00Z"}
            """;
        var handler = new RecordingHandler(
            Json(HttpStatusCode.Created, releaseJson),
            Json(HttpStatusCode.OK, invalidateJson),
            Json(HttpStatusCode.OK, revokeJson));
        using var api = CreateClient(handler);

        var release = await api.CreateOffsetLoaderReleaseAsync(
            "run:1", new("machine-1", "nc-1", "tools-1"), "windows-1", 43);
        var invalidated = await api.InvalidateCncVerificationAsync(
            "run:1", new("machine-1", "Fixture changed"), "windows-1", 43);
        var revoked = await api.RevokeCurrentOffsetLoaderAsync(
            "run:1", new("machine-1", "Offsets invalid"), "windows-1", 43);

        Assert.True(release.IsCurrent);
        Assert.Equal("INVALIDATE_VERIFICATION", invalidated.Action);
        Assert.Equal("REVOKE_CURRENT_OFFSET_LOADER", revoked.Action);
        Assert.Equal("/api/v1/production-runs/run%3A1/offset-loader-releases", handler.Requests[0].Path);
        Assert.Equal("/api/v1/production-runs/run%3A1/verification/invalidate", handler.Requests[1].Path);
        Assert.Equal("/api/v1/production-runs/run%3A1/offset-loader/current/revoke", handler.Requests[2].Path);
        Assert.All(handler.Requests, request => Assert.Equal("43", request.Generation));
        Assert.Contains("\"machineId\":\"machine-1\"", handler.Requests[0].Body, StringComparison.Ordinal);
        Assert.Contains("\"reason\":\"Fixture changed\"", handler.Requests[1].Body, StringComparison.Ordinal);
        Assert.Contains("\"reason\":\"Offsets invalid\"", handler.Requests[2].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Qc_queue_read_and_decision_use_typed_contract_and_user_edit_authority()
    {
        const string queueJson = """
            {"items":[{"productionRunId":"run:1","machineId":"machine-1",
            "machineNumber":"M-1","machineName":"Mill One","part":"PN-100",
            "operation":"OP10 Rough","receivedAt":"2026-08-26T10:00:00Z",
            "setupistId":"setup-1","setupistName":"Setup Worker"}]}
            """;
        const string decisionJson = """
            {"eventId":"qc-event-1","productionRunId":"run:1","decision":"PASS",
            "resultingStatus":"READY_FOR_PRODUCTION","userId":"qc-user",
            "reason":"Accepted","timestamp":"2026-08-26T10:05:00Z",
            "productionApprovedAt":"2026-08-26T10:05:00Z"}
            """;
        var handler = new RecordingHandler(
            Json(HttpStatusCode.OK, queueJson),
            Json(HttpStatusCode.OK, decisionJson));
        using var api = CreateClient(handler);

        var queue = await api.ListQcQueueAsync();
        var result = await api.DecideQcAsync(
            "run:1", new("PASS", "Accepted"), "qc-client", "qc-user", 12);

        Assert.Equal("PN-100", Assert.Single(queue).Part);
        Assert.Equal("READY_FOR_PRODUCTION", result.ResultingStatus);
        Assert.Equal("/api/v1/qc-queue", handler.Requests[0].Path);
        Assert.Equal("/api/v1/qc-queue/run%3A1/decision", handler.Requests[1].Path);
        Assert.Equal("qc-client", handler.Requests[1].ClientId);
        Assert.Equal("qc-user", handler.Requests[1].UserId);
        Assert.Equal("12", handler.Requests[1].Generation);
        Assert.Contains("\"decision\":\"PASS\"", handler.Requests[1].Body, StringComparison.Ordinal);
        Assert.Contains("\"reason\":\"Accepted\"", handler.Requests[1].Body, StringComparison.Ordinal);
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
