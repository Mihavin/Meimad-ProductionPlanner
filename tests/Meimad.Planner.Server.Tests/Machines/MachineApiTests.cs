using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Meimad.Planner.Server.Tests.Machines;

public sealed class MachineApiTests
{
    [Fact]
    public async Task Empty_machine_can_be_edited_and_deactivated_but_assigned_machine_cannot()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedCalendarAndOperationsAsync(application.Services);
            await GrantEditModeAsync(application.Services);
            AddEditHeaders(client);

            var emptyId = await CreateMachineAsync(client, "M-EMPTY", "mill", []);
            using var empty = await client.GetAsync($"/api/v1/machines/{emptyId}");
            using var deactivate = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/machines/{emptyId}")
            { Content = JsonContent.Create(new { name = "Edited Empty Machine", isActive = false }) };
            deactivate.Headers.IfMatch.Add(new EntityTagHeaderValue(empty.Headers.ETag!.Tag));
            using var deactivated = await client.SendAsync(deactivate);
            Assert.Equal(HttpStatusCode.OK, deactivated.StatusCode);
            using var deactivatedJson = JsonDocument.Parse(await deactivated.Content.ReadAsStringAsync());
            Assert.Equal("Edited Empty Machine", deactivatedJson.RootElement.GetProperty("name").GetString());
            Assert.False(deactivatedJson.RootElement.GetProperty("isActive").GetBoolean());

            var assignedId = await CreateMachineAsync(client, "M-ASSIGNED", "mill", []);
            using var assignment = await client.PutAsJsonAsync(
                "/api/v1/batch-operations/op-a/assignment",
                new { machineId = assignedId, backlogPosition = 0 });
            assignment.EnsureSuccessStatusCode();
            using var assigned = await client.GetAsync($"/api/v1/machines/{assignedId}");
            using var unsafeDeactivate = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/machines/{assignedId}")
            { Content = JsonContent.Create(new { isActive = false }) };
            unsafeDeactivate.Headers.IfMatch.Add(new EntityTagHeaderValue(assigned.Headers.ETag!.Tag));
            using var blocked = await client.SendAsync(unsafeDeactivate);
            Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
            using var blockedJson = JsonDocument.Parse(await blocked.Content.ReadAsStringAsync());
            Assert.Equal("assigned_operation_incompatible", blockedJson.RootElement.GetProperty("error").GetProperty("code").GetString());

            using var unchanged = await client.GetAsync($"/api/v1/machines/{assignedId}");
            using var unchangedJson = JsonDocument.Parse(await unchanged.Content.ReadAsStringAsync());
            Assert.True(unchangedJson.RootElement.GetProperty("isActive").GetBoolean());
            Assert.Equal(1, unchangedJson.RootElement.GetProperty("backlogCount").GetInt32());
        });
    }

    [Fact]
    public async Task Machine_catalog_and_assignment_commands_work_over_http()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedCalendarAndOperationsAsync(application.Services);
            await GrantEditModeAsync(application.Services);
            AddEditHeaders(client);

            var machineId = await CreateMachineAsync(client, "M-API-1", "mill", ["probe"]);
            using var listResponse = await client.GetAsync("/api/v1/machines");
            Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
            using var listDocument = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
            Assert.Single(listDocument.RootElement.GetProperty("items").EnumerateArray());

            using var assignA = await client.PutAsJsonAsync(
                "/api/v1/batch-operations/op-a/assignment",
                new { machineId, backlogPosition = 0 });
            Assert.Equal(HttpStatusCode.Created, assignA.StatusCode);
            using var compatibleAudit = await client.GetAsync(
                "/api/v1/batch-operations/op-a/assignment-overrides");
            compatibleAudit.EnsureSuccessStatusCode();
            using (var compatibleAuditJson = JsonDocument.Parse(
                await compatibleAudit.Content.ReadAsStringAsync()))
            {
                Assert.Empty(
                    compatibleAuditJson.RootElement.GetProperty("items").EnumerateArray());
            }
            using var assignB = await client.PutAsJsonAsync(
                "/api/v1/batch-operations/op-b/assignment",
                new { machineId, backlogPosition = 1 });
            Assert.Equal(HttpStatusCode.Created, assignB.StatusCode);

            using var moveB = await client.PutAsJsonAsync(
                "/api/v1/batch-operations/op-b/assignment",
                new { machineId, backlogPosition = 0 });
            Assert.Equal(HttpStatusCode.OK, moveB.StatusCode);

            using var backlogResponse = await client.GetAsync($"/api/v1/machines/{machineId}/backlog");
            Assert.Equal(HttpStatusCode.OK, backlogResponse.StatusCode);
            using var backlogDocument = JsonDocument.Parse(
                await backlogResponse.Content.ReadAsStringAsync());
            var items = backlogDocument.RootElement.GetProperty("items");
            Assert.Equal("op-b", items[0].GetProperty("assignment").GetProperty("batchOperationId").GetString());
            Assert.Equal("op-a", items[1].GetProperty("assignment").GetProperty("batchOperationId").GetString());

            using var reorderEvents = await client.GetAsync("/api/v1/event-log?eventType=manual_backlog_reorder");
            reorderEvents.EnsureSuccessStatusCode();
            using var reorderJson = JsonDocument.Parse(await reorderEvents.Content.ReadAsStringAsync());
            var reorder = Assert.Single(reorderJson.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal("op-b", reorder.GetProperty("relatedEntityIds").GetProperty("batchOperationId").GetString());
            Assert.Equal(1, reorder.GetProperty("beforeData").GetProperty("backlogPosition").GetInt32());
            Assert.Equal(0, reorder.GetProperty("afterData").GetProperty("backlogPosition").GetInt32());

            using var boardResponse = await client.GetAsync("/api/v1/planning-board");
            Assert.Equal(HttpStatusCode.OK, boardResponse.StatusCode);
            using var boardDocument = JsonDocument.Parse(
                await boardResponse.Content.ReadAsStringAsync());
            Assert.Equal(
                "unavailable",
                boardDocument.RootElement.GetProperty("conflictCalculationStatus").GetString());
            Assert.Empty(boardDocument.RootElement.GetProperty("conflicts").EnumerateArray());
            Assert.Equal(
                "op-laser",
                boardDocument.RootElement.GetProperty("pool")[0]
                    .GetProperty("batchOperationId").GetString());
            var machineBacklog = boardDocument.RootElement.GetProperty("machines")[0]
                .GetProperty("backlog");
            Assert.Equal("op-b", machineBacklog[0].GetProperty("batchOperationId").GetString());
            Assert.Equal("op-a", machineBacklog[1].GetProperty("batchOperationId").GetString());

            using var unassign = await client.DeleteAsync("/api/v1/batch-operations/op-b/assignment");
            Assert.Equal(HttpStatusCode.NoContent, unassign.StatusCode);

            using var machineResponse = await client.GetAsync($"/api/v1/machines/{machineId}");
            using var machineDocument = JsonDocument.Parse(
                await machineResponse.Content.ReadAsStringAsync());
            Assert.Equal(1, machineDocument.RootElement.GetProperty("backlogCount").GetInt32());
            Assert.True(machineDocument.RootElement.GetProperty("isActive").GetBoolean());
            Assert.True(machineDocument.RootElement.GetProperty("displayEnabled").GetBoolean());
        });
    }

    [Fact]
    public async Task Cross_type_assignment_requires_reason_then_logs_confirmed_override()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedCalendarAndOperationsAsync(application.Services);
            await GrantEditModeAsync(application.Services);
            AddEditHeaders(client);
            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using (var connection = await database.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    UPDATE case_operations SET required_machine_type = '3-axis'
                    WHERE id = 'case-op-laser';
                    UPDATE batch_operations SET required_machine_type = '3-axis'
                    WHERE id = 'op-laser';
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var machineId = await CreateMachineAsync(
                client, "M-5AXIS", "5-axis milling", ["3-axis"]);

            using var warning = await client.PutAsJsonAsync(
                "/api/v1/batch-operations/op-laser/assignment",
                new { machineId, backlogPosition = 0 });
            Assert.Equal(HttpStatusCode.Conflict, warning.StatusCode);
            using (var warningJson = JsonDocument.Parse(await warning.Content.ReadAsStringAsync()))
            {
                Assert.Equal(
                    "machine_type_override_required",
                    warningJson.RootElement.GetProperty("error").GetProperty("code").GetString());
            }

            using var backlog = await client.GetAsync($"/api/v1/machines/{machineId}/backlog");
            using var document = JsonDocument.Parse(await backlog.Content.ReadAsStringAsync());
            Assert.Empty(document.RootElement.GetProperty("items").EnumerateArray());

            using var missingReason = await client.PutAsJsonAsync(
                "/api/v1/batch-operations/op-laser/assignment",
                new
                {
                    machineId,
                    backlogPosition = 0,
                    compatibilityOverride = new { confirmed = true, reason = "  " }
                });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, missingReason.StatusCode);
            using (var missingReasonJson = JsonDocument.Parse(
                await missingReason.Content.ReadAsStringAsync()))
            {
                Assert.Equal(
                    "reason_required",
                    missingReasonJson.RootElement.GetProperty("error").GetProperty("details")[0]
                        .GetProperty("code").GetString());
            }

            const string reason = "Use the 5-axis spindle while the 3-axis Machine is under maintenance.";
            using var confirmed = await client.PutAsJsonAsync(
                "/api/v1/batch-operations/op-laser/assignment",
                new
                {
                    machineId,
                    backlogPosition = 0,
                    compatibilityOverride = new { confirmed = true, reason }
                });
            Assert.Equal(HttpStatusCode.Created, confirmed.StatusCode);

            using var assignedBacklog = await client.GetAsync($"/api/v1/machines/{machineId}/backlog");
            using var assignedDocument = JsonDocument.Parse(
                await assignedBacklog.Content.ReadAsStringAsync());
            Assert.Equal(
                "op-laser",
                assignedDocument.RootElement.GetProperty("items")[0]
                    .GetProperty("assignment").GetProperty("batchOperationId").GetString());

            using var audit = await client.GetAsync(
                "/api/v1/batch-operations/op-laser/assignment-overrides");
            audit.EnsureSuccessStatusCode();
            using var auditJson = JsonDocument.Parse(await audit.Content.ReadAsStringAsync());
            var entry = Assert.Single(auditJson.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal("3-axis", entry.GetProperty("requiredMachineType").GetString());
            Assert.Equal("5-axis milling", entry.GetProperty("selectedMachineType").GetString());
            Assert.Equal(reason, entry.GetProperty("reason").GetString());
            Assert.Equal("machine-api-client", entry.GetProperty("confirmedByClientId").GetString());
            Assert.Equal("machine-api-user", entry.GetProperty("confirmedByUserId").GetString());
            Assert.NotEqual(default, entry.GetProperty("confirmedAt").GetDateTimeOffset());

            using var events = await client.GetAsync("/api/v1/event-log?eventType=cross_machine_type_override");
            events.EnsureSuccessStatusCode();
            using var eventsJson = JsonDocument.Parse(await events.Content.ReadAsStringAsync());
            var logged = Assert.Single(eventsJson.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal("machine-api-user", logged.GetProperty("user").GetString());
            Assert.Equal("machine_type_incompatible", logged.GetProperty("reasonCode").GetString());
            Assert.Equal("op-laser", logged.GetProperty("relatedEntityIds").GetProperty("batchOperationId").GetString());
            Assert.Equal(reason, logged.GetProperty("comment").GetString());
            Assert.Equal("3-axis", logged.GetProperty("beforeData").GetProperty("requiredMachineType").GetString());

            await using (var verificationConnection = await database.OpenConnectionAsync())
            {
                Assert.Equal("3-axis", await ScalarAsync(verificationConnection,
                    "SELECT required_machine_type FROM case_operations WHERE id='case-op-laser';"));
                Assert.Equal("3-axis", await ScalarAsync(verificationConnection,
                    "SELECT required_machine_type FROM batch_operations WHERE id='op-laser';"));
                await using var timelineSetup = verificationConnection.CreateCommand();
                timelineSetup.CommandText = """
                    UPDATE working_calendars SET time_zone_id='UTC',
                      calendar_json='{"availability":[{"startsAt":"2026-08-11T08:00:00Z","endsAt":"2026-08-11T18:00:00Z"}],"usages":["machine","setup_worker"]}'
                    WHERE id='calendar-1';
                    UPDATE setup_calendar_settings SET working_calendar_id='calendar-1',legacy_fallback_enabled=0 WHERE id=1;
                    UPDATE batch_operations SET setup_seconds=0,cycle_seconds=3600 WHERE id='op-laser';
                    """;
                await timelineSetup.ExecuteNonQueryAsync();
            }
            using var timeline = await client.GetAsync(
                "/api/v1/timeline?from=2026-08-11T08:00:00Z&to=2026-08-11T18:00:00Z&asOf=2026-08-11T08:00:00Z");
            timeline.EnsureSuccessStatusCode();
            using var timelineJson = JsonDocument.Parse(await timeline.Content.ReadAsStringAsync());
            Assert.Contains(timelineJson.RootElement.GetProperty("machines").EnumerateArray()
                .SelectMany(machine => machine.GetProperty("intervals").EnumerateArray()), interval =>
                    interval.GetProperty("operationId").ValueKind == JsonValueKind.String
                    && interval.GetProperty("operationId").GetString() == "op-laser"
                    && interval.GetProperty("type").GetString() == "operation"
                    && interval.GetProperty("detail").GetString()!
                        .Contains("Production", StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task Planning_mode_patch_updates_the_existing_assignment_once_and_is_concurrent_and_audited()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedCalendarAndOperationsAsync(application.Services);
            await GrantEditModeAsync(application.Services);
            AddEditHeaders(client);
            var machineId = await CreateMachineAsync(client, "M-MODE", "mill", ["mill"]);

            using var createdResponse = await client.PutAsJsonAsync(
                "/api/v1/batch-operations/op-a/assignment",
                new { machineId, backlogPosition = 0 });
            Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
            using var createdJson = JsonDocument.Parse(
                await createdResponse.Content.ReadAsStringAsync());
            var assignmentId = createdJson.RootElement
                .GetProperty("machineAssignmentId").GetString()!;
            var originalVersion = createdJson.RootElement.GetProperty("version").GetInt32();
            var originalTag = createdResponse.Headers.ETag?.Tag;
            Assert.Equal("manual", createdJson.RootElement.GetProperty("planningMode").GetString());
            Assert.Equal(
                $"\"machine-assignment:{assignmentId}:v{originalVersion}\"",
                originalTag);

            using var secondResponse = await client.PutAsJsonAsync(
                "/api/v1/batch-operations/op-b/assignment",
                new { machineId, backlogPosition = 1 });
            secondResponse.EnsureSuccessStatusCode();
            using var secondJson = JsonDocument.Parse(
                await secondResponse.Content.ReadAsStringAsync());
            var secondAssignmentId = secondJson.RootElement
                .GetProperty("machineAssignmentId").GetString()!;

            var database = application.Services.GetRequiredService<SqliteDatabase>();
            await using var beforeConnection = await database.OpenConnectionAsync();
            var secondVersionBefore = await ScalarAsync(beforeConnection,
                $"SELECT version FROM machine_assignments WHERE id='{secondAssignmentId}';");
            var secondUpdatedBefore = await ScalarAsync(beforeConnection,
                $"SELECT updated_at FROM machine_assignments WHERE id='{secondAssignmentId}';");

            using var missingPrecondition = await client.PatchAsJsonAsync(
                $"/api/v1/machine-assignments/{assignmentId}",
                new { planningMode = "backward" });
            Assert.Equal((HttpStatusCode)428, missingPrecondition.StatusCode);

            using (var invalidRequest = CreatePlanningModePatch(
                       assignmentId, originalTag!, "Backward"))
            using (var invalid = await client.SendAsync(invalidRequest))
            {
                Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
            }

            using var patchRequest = CreatePlanningModePatch(
                assignmentId, originalTag!, "backward");
            using var patched = await client.SendAsync(patchRequest);
            Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
            using var patchedJson = JsonDocument.Parse(await patched.Content.ReadAsStringAsync());
            var patchedVersion = patchedJson.RootElement.GetProperty("version").GetInt32();
            var patchedTag = patched.Headers.ETag?.Tag;
            Assert.Equal(assignmentId, patchedJson.RootElement
                .GetProperty("machineAssignmentId").GetString());
            Assert.Equal("op-a", patchedJson.RootElement.GetProperty("batchOperationId").GetString());
            Assert.Equal(machineId, patchedJson.RootElement.GetProperty("machineId").GetString());
            Assert.Equal(0, patchedJson.RootElement.GetProperty("backlogPosition").GetInt32());
            Assert.Equal("backward", patchedJson.RootElement.GetProperty("planningMode").GetString());
            Assert.Equal(originalVersion + 1, patchedVersion);

            await using (var afterConnection = await database.OpenConnectionAsync())
            {
                Assert.Equal("backward", await ScalarAsync(afterConnection,
                    $"SELECT planning_mode FROM machine_assignments WHERE id='{assignmentId}';"));
                Assert.Equal(secondVersionBefore, await ScalarAsync(afterConnection,
                    $"SELECT version FROM machine_assignments WHERE id='{secondAssignmentId}';"));
                Assert.Equal(secondUpdatedBefore, await ScalarAsync(afterConnection,
                    $"SELECT updated_at FROM machine_assignments WHERE id='{secondAssignmentId}';"));
            }

            using var board = await client.GetAsync("/api/v1/planning-board");
            board.EnsureSuccessStatusCode();
            using var boardJson = JsonDocument.Parse(await board.Content.ReadAsStringAsync());
            var boardOperation = Assert.Single(boardJson.RootElement.GetProperty("machines")
                .EnumerateArray()
                .Single(machine => machine.GetProperty("machineId").GetString() == machineId)
                .GetProperty("backlog")
                .EnumerateArray(),
                operation => operation.GetProperty("batchOperationId").GetString() == "op-a");
            Assert.Equal(assignmentId, boardOperation.GetProperty("machineAssignmentId").GetString());
            Assert.Equal(patchedVersion, boardOperation.GetProperty("assignmentVersion").GetInt32());
            Assert.Equal("backward", boardOperation.GetProperty("planningMode").GetString());

            using var events = await client.GetAsync(
                "/api/v1/event-log?eventType=machine_assignment_planning_mode_changed");
            events.EnsureSuccessStatusCode();
            using var eventsJson = JsonDocument.Parse(await events.Content.ReadAsStringAsync());
            var logged = Assert.Single(eventsJson.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal("machine-api-user", logged.GetProperty("user").GetString());
            Assert.Equal(assignmentId, logged.GetProperty("relatedEntityIds")
                .GetProperty("machineAssignmentId").GetString());
            Assert.Equal("manual", logged.GetProperty("beforeData").GetProperty("planningMode").GetString());
            Assert.Equal("backward", logged.GetProperty("afterData").GetProperty("planningMode").GetString());

            var updatedAtBeforeNoOp = patchedJson.RootElement.GetProperty("updatedAt").GetDateTimeOffset();
            using (var noOpRequest = CreatePlanningModePatch(
                       assignmentId, patchedTag!, "backward"))
            using (var noOp = await client.SendAsync(noOpRequest))
            {
                noOp.EnsureSuccessStatusCode();
                using var noOpJson = JsonDocument.Parse(await noOp.Content.ReadAsStringAsync());
                Assert.Equal(patchedVersion, noOpJson.RootElement.GetProperty("version").GetInt32());
                Assert.Equal(updatedAtBeforeNoOp, noOpJson.RootElement.GetProperty("updatedAt").GetDateTimeOffset());
                Assert.Equal(patchedTag, noOp.Headers.ETag?.Tag);
            }

            using (var staleRequest = CreatePlanningModePatch(
                       assignmentId, originalTag!, "manual"))
            using (var stale = await client.SendAsync(staleRequest))
            {
                Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
            }

            using (var unknownRequest = CreatePlanningModePatch(
                       "missing-assignment",
                       "\"machine-assignment:missing-assignment:v1\"",
                       "forward"))
            using (var unknown = await client.SendAsync(unknownRequest))
            {
                Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
            }

            using var movedResponse = await client.PutAsJsonAsync(
                "/api/v1/batch-operations/op-a/assignment",
                new { machineId, backlogPosition = 1 });
            movedResponse.EnsureSuccessStatusCode();
            using var movedJson = JsonDocument.Parse(await movedResponse.Content.ReadAsStringAsync());
            Assert.Equal(assignmentId, movedJson.RootElement.GetProperty("machineAssignmentId").GetString());
            Assert.Equal("backward", movedJson.RootElement.GetProperty("planningMode").GetString());

            using var movedBackResponse = await client.PutAsJsonAsync(
                "/api/v1/batch-operations/op-a/assignment",
                new { machineId, backlogPosition = 0 });
            movedBackResponse.EnsureSuccessStatusCode();
            using var movedBackJson = JsonDocument.Parse(
                await movedBackResponse.Content.ReadAsStringAsync());
            var runningTag = movedBackResponse.Headers.ETag?.Tag;

            using var started = await client.PostAsync(
                "/api/v1/batch-operations/op-a/start", null);
            started.EnsureSuccessStatusCode();
            using var runningRequest = CreatePlanningModePatch(
                assignmentId, runningTag!, "forward");
            using var running = await client.SendAsync(runningRequest);
            Assert.Equal(HttpStatusCode.Conflict, running.StatusCode);

            using var suspended = await client.PostAsJsonAsync(
                "/api/v1/batch-operations/op-a/suspend",
                new { reasonType = "other", comment = "Mode persistence verification" });
            suspended.EnsureSuccessStatusCode();
            using var reset = await client.PostAsync(
                "/api/v1/batch-operations/op-a/reset", null);
            reset.EnsureSuccessStatusCode();

            using var resetBacklog = await client.GetAsync(
                $"/api/v1/machines/{machineId}/backlog");
            resetBacklog.EnsureSuccessStatusCode();
            using var resetBacklogJson = JsonDocument.Parse(
                await resetBacklog.Content.ReadAsStringAsync());
            var resetAssignment = resetBacklogJson.RootElement.GetProperty("items")
                .EnumerateArray()
                .Single(item => item.GetProperty("assignment")
                    .GetProperty("machineAssignmentId").GetString() == assignmentId)
                .GetProperty("assignment");
            Assert.Equal("backward", resetAssignment.GetProperty("planningMode").GetString());
            Assert.Equal(
                movedBackJson.RootElement.GetProperty("version").GetInt32(),
                resetAssignment.GetProperty("version").GetInt32());

            using var finalEvents = await client.GetAsync(
                "/api/v1/event-log?eventType=machine_assignment_planning_mode_changed");
            finalEvents.EnsureSuccessStatusCode();
            using var finalEventsJson = JsonDocument.Parse(
                await finalEvents.Content.ReadAsStringAsync());
            Assert.Single(finalEventsJson.RootElement.GetProperty("items").EnumerateArray());
        });
    }

    [Fact]
    public async Task Machine_picture_path_is_stored_as_text_and_served_through_api()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedCalendarAndOperationsAsync(application.Services);
            await GrantEditModeAsync(application.Services);
            AddEditHeaders(client);
            var picturePath = Path.Combine(
                Path.GetTempPath(),
                $"meimad-machine-{Guid.NewGuid():N}.png");
            var pictureBytes = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
            await File.WriteAllBytesAsync(picturePath, pictureBytes);
            try
            {
                using var create = await client.PostAsJsonAsync(
                    "/api/v1/machines",
                    new
                    {
                        number = "M-PICTURE",
                        name = "Picture Machine",
                        processType = "mill",
                        axisType = "3-axis",
                        capabilities = new[] { "probe" },
                        workingCalendarId = "calendar-1",
                        isActive = true,
                        displayEnabled = true,
                        picturePath
                    });
                Assert.Equal(HttpStatusCode.Created, create.StatusCode);
                using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
                var machineId = created.RootElement.GetProperty("machineId").GetString()!;
                Assert.Equal(picturePath, created.RootElement.GetProperty("picturePath").GetString());

                using var picture = await client.GetAsync($"/api/v1/machines/{machineId}/picture");
                Assert.Equal(HttpStatusCode.OK, picture.StatusCode);
                Assert.Equal("image/png", picture.Content.Headers.ContentType?.MediaType);
                Assert.Equal(pictureBytes, await picture.Content.ReadAsByteArrayAsync());

                var database = application.Services.GetRequiredService<SqliteDatabase>();
                await using var connection = await database.OpenConnectionAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT picture_reference, typeof(picture_reference) FROM machines WHERE id = $id;";
                command.Parameters.AddWithValue("$id", machineId);
                await using var reader = await command.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(picturePath, reader.GetString(0));
                Assert.Equal("text", reader.GetString(1));
            }
            finally
            {
                File.Delete(picturePath);
            }
        });
    }

    [Fact]
    public async Task Missing_machine_picture_returns_not_found_without_requiring_file_at_create_time()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedCalendarAndOperationsAsync(application.Services);
            await GrantEditModeAsync(application.Services);
            AddEditHeaders(client);
            using var create = await client.PostAsJsonAsync(
                "/api/v1/machines",
                new
                {
                    number = "M-MISSING-PICTURE",
                    name = "Missing Picture Machine",
                    processType = "mill",
                    axisType = (string?)null,
                    capabilities = Array.Empty<string>(),
                    workingCalendarId = "calendar-1",
                    isActive = true,
                    displayEnabled = true,
                    picturePath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.png")
                });
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
            using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
            var machineId = created.RootElement.GetProperty("machineId").GetString()!;

            using var picture = await client.GetAsync($"/api/v1/machines/{machineId}/picture");
            Assert.Equal(HttpStatusCode.NotFound, picture.StatusCode);
        });
    }

    [Fact]
    public async Task Machine_execution_configuration_uses_explicit_postprocessor_mapping()
    {
        await RunWithServerAsync(async (application, client) =>
        {
            await SeedCalendarAndOperationsAsync(application.Services);
            await GrantEditModeAsync(application.Services);
            AddEditHeaders(client);

            static async Task<string> CreatePostprocessorAsync(HttpClient http, string name)
            {
                using var response = await http.PostAsJsonAsync(
                    "/api/v1/postprocessors",
                    new { name, description = $"{name} configuration", isActive = true });
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                return document.RootElement.GetProperty("postprocessorId").GetString()!;
            }

            var threeAxis = await CreatePostprocessorAsync(client, "Doosan 3X");
            var fourAxis = await CreatePostprocessorAsync(client, "Doosan 4X");

            using var createCnc = await client.PostAsJsonAsync(
                "/api/v1/machines",
                new
                {
                    number = "M-CNC",
                    name = "CNC machine",
                    processType = "mill",
                    axisType = "4-axis",
                    capabilities = Array.Empty<string>(),
                    workingCalendarId = "calendar-1",
                    isActive = true,
                    displayEnabled = true,
                    executionMode = "CNC_GCODE",
                    supportedPostprocessorIds = new[] { threeAxis, fourAxis },
                    usableToolPositions = 30,
                    rapidRateMillimetersPerMinute = 24000,
                    toolChangeTimeSeconds = 4.5,
                    machineTimeFactor = 1.0
                });
            Assert.Equal(HttpStatusCode.Created, createCnc.StatusCode);
            string cncMachineId;
            using (var created = JsonDocument.Parse(await createCnc.Content.ReadAsStringAsync()))
            {
                var root = created.RootElement;
                cncMachineId = root.GetProperty("machineId").GetString()!;
                Assert.Equal("CNC_GCODE", root.GetProperty("executionMode").GetString());
                Assert.Equal(2, root.GetProperty("supportedPostprocessorIds").GetArrayLength());
                Assert.Equal(30, root.GetProperty("usableToolPositions").GetInt32());
                Assert.Equal(24000, root.GetProperty("rapidRateMillimetersPerMinute").GetDouble());
                Assert.Equal(4.5, root.GetProperty("toolChangeTimeSeconds").GetDouble());
                Assert.Equal(1.0, root.GetProperty("machineTimeFactor").GetDouble());
            }

            using var unsupported = await client.PostAsJsonAsync(
                "/api/v1/machines",
                new
                {
                    number = "M-INVALID-POST",
                    name = "Invalid mapping",
                    processType = "mill",
                    capabilities = Array.Empty<string>(),
                    workingCalendarId = "calendar-1",
                    isActive = true,
                    displayEnabled = true,
                    executionMode = "CNC_GCODE",
                    supportedPostprocessorIds = new[] { "not-a-managed-postprocessor" }
                });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, unsupported.StatusCode);
            using (var error = JsonDocument.Parse(await unsupported.Content.ReadAsStringAsync()))
            {
                Assert.Equal("invalid_postprocessor", error.RootElement.GetProperty("error").GetProperty("code").GetString());
            }

            using var manual = await client.PostAsJsonAsync(
                "/api/v1/machines",
                new
                {
                    number = "M-MANUAL",
                    name = "Manual machine",
                    processType = "manual",
                    capabilities = Array.Empty<string>(),
                    workingCalendarId = "calendar-1",
                    isActive = true,
                    displayEnabled = true
                });
            manual.EnsureSuccessStatusCode();
            using var manualDocument = JsonDocument.Parse(await manual.Content.ReadAsStringAsync());
            Assert.Equal("MANUAL", manualDocument.RootElement.GetProperty("executionMode").GetString());
            Assert.Equal(1.0, manualDocument.RootElement.GetProperty("machineTimeFactor").GetDouble());
            Assert.Empty(manualDocument.RootElement.GetProperty("supportedPostprocessorIds").EnumerateArray());

            using var mappedDelete = await client.DeleteAsync($"/api/v1/postprocessors/{threeAxis}");
            Assert.Equal(HttpStatusCode.Conflict, mappedDelete.StatusCode);

            using var deleteMachine = await client.DeleteAsync($"/api/v1/machines/{cncMachineId}");
            Assert.Equal(HttpStatusCode.NoContent, deleteMachine.StatusCode);
            using var nowUnmappedDelete = await client.DeleteAsync($"/api/v1/postprocessors/{threeAxis}");
            Assert.Equal(HttpStatusCode.NoContent, nowUnmappedDelete.StatusCode);
        });
    }

    private static async Task<string> CreateMachineAsync(
        HttpClient client,
        string number,
        string processType,
        string[] capabilities)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/v1/machines",
            new
            {
                number,
                name = $"Machine {number}",
                processType,
                axisType = (string?)null,
                capabilities,
                workingCalendarId = "calendar-1",
                isActive = true,
                displayEnabled = true
            });
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("machineId").GetString()!;
    }

    private static void AddEditHeaders(HttpClient client)
    {
        client.DefaultRequestHeaders.Add("X-Meimad-Client-Id", "machine-api-client");
        client.DefaultRequestHeaders.Add("X-Meimad-Edit-Generation", "1");
    }

    private static HttpRequestMessage CreatePlanningModePatch(
        string assignmentId,
        string entityTag,
        string planningMode)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Patch,
            $"/api/v1/machine-assignments/{assignmentId}")
        {
            Content = JsonContent.Create(new { planningMode })
        };
        request.Headers.TryAddWithoutValidation("If-Match", entityTag);
        return request;
    }

    private static async Task SeedCalendarAndOperationsAsync(IServiceProvider services)
    {
        var database = services.GetRequiredService<SqliteDatabase>();
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO working_calendars (id, name, time_zone_id)
            VALUES ('calendar-1', 'Factory', 'Asia/Jerusalem');

            INSERT INTO cases (id, part_number, name, working_folder_path)
            VALUES ('case-1', 'PN-API-M', 'Machine API', 'C:\Cases\PN-API-M');

            INSERT INTO production_batches (
                id, case_id, batch_number, status, planned_quantity)
            VALUES ('batch-1', 'case-1', 'B-API-M', 'waiting', 1);

            INSERT INTO case_operations (
                id, case_id, operation_number, route_position, name, required_machine_type)
            VALUES
                ('case-op-a', 'case-1', 10, 0, 'A', 'mill'),
                ('case-op-b', 'case-1', 20, 1, 'B', 'mill'),
                ('case-op-laser', 'case-1', 30, 2, 'Laser', 'laser');

            INSERT INTO batch_operations (
                id, production_batch_id, source_case_operation_id,
                operation_number, route_position, name, required_machine_type, status)
            VALUES
                ('op-a', 'batch-1', 'case-op-a', 10, 0, 'A', 'mill', 'not_started'),
                ('op-b', 'batch-1', 'case-op-b', 20, 1, 'B', 'mill', 'not_started'),
                ('op-laser', 'batch-1', 'case-op-laser', 30, 2, 'Laser', 'laser', 'not_started');
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task GrantEditModeAsync(IServiceProvider services)
    {
        var database = services.GetRequiredService<SqliteDatabase>();
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE edit_tokens
            SET holder_client_id = 'machine-api-client',
                holder_user_id = 'machine-api-user',
                generation = 1,
                acquired_at = '2026-08-11T00:00:00Z',
                version = version + 1,
                updated_at = '2026-08-11T00:00:00Z'
            WHERE id = 1;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private static async Task RunWithServerAsync(Func<WebApplication, HttpClient, Task> test)
    {
        var directoryPath = Path.Combine(
            Path.GetTempPath(),
            "MeimadPlanner.MachineApi.Tests",
            Guid.NewGuid().ToString("N"));
        var application = ServerApplication.Build(
            [
                "--Server:Host=127.0.0.1",
                "--Server:Port=5099",
                $"--Database:Path={Path.Combine(directoryPath, "api-test.db")}"
            ],
            webHost => webHost.UseTestServer());
        try
        {
            await application.StartAsync();
            using var client = application.GetTestClient();
            await test(application, client);
            await application.StopAsync();
        }
        finally
        {
            await application.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }
}
