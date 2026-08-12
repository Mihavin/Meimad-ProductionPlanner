using System.Net;
using System.Net.Http;
using System.Text;
using Meimad.Planner.Client.Windows.Api;

namespace Meimad.Planner.Client.Windows.Tests.Api;

public sealed class PlannerApiClientTests
{
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
            """));
        using var api = CreateClient(handler);

        var result = await api.ChangeOperationExecutionAsync(
            "operation/1", "start", "windows-01", 21);

        Assert.Equal("in_progress", result.Status);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("/api/v1/batch-operations/operation%2F1/start", handler.Requests[0].Path);
        Assert.Equal("windows-01", handler.Requests[0].ClientId);
        Assert.Equal("21", handler.Requests[0].Generation);
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
                "06:00", "22:00"),
            "windows-01",
            19);

        Assert.Equal("calendar-day", calendars.Single().WorkingCalendarId);
        Assert.Equal("calendar-new", created.WorkingCalendarId);
        Assert.Equal("/api/v1/working-calendars", handler.Requests[0].Path);
        Assert.Equal(HttpMethod.Post, handler.Requests[1].Method);
        Assert.Equal("windows-01", handler.Requests[1].ClientId);
        Assert.Equal("19", handler.Requests[1].Generation);
        Assert.Contains("\"shiftEndsAtLocal\":\"22:00\"", handler.Requests[1].Body, StringComparison.Ordinal);
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
                  "pool": [],
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
                  "type":"production","machineId":"machine-1","operationId":"op-1",
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

        Assert.Equal("production", result.Machines[0].Intervals[0].Type);
        Assert.Equal("B-1", result.Batches[0].BatchNumber);
        Assert.Contains("from=2026-08-11T08%3A00%3A00", handler.Requests[0].Path, StringComparison.Ordinal);
        Assert.Contains("to=2026-08-12T08%3A00%3A00", handler.Requests[0].Path, StringComparison.Ordinal);
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
