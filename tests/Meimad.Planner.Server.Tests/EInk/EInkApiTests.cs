using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Meimad.Planner.Server.Application.Cnc;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Meimad.Planner.Server.Tests.EInk;

public sealed class EInkApiTests
{
    private const string DeviceId = "device-eink-1";
    private const string TabletId = "3041";

    [Fact]
    public async Task Device_reads_version_screen_manifest_file_and_time_configuration()
    {
        await RunWithServerAsync(async (application, client, packageRoot) =>
        {
            var fileBytes = await SeedAsync(application.Services, packageRoot);
            using var versionRequest = Get($"/api/v1/eink/tablets/{TabletId}/version");
            using var versionResponse = await client.SendAsync(versionRequest);
            Assert.Equal(HttpStatusCode.OK, versionResponse.StatusCode);
            Assert.NotNull(versionResponse.Headers.ETag);
            using var version = JsonDocument.Parse(await versionResponse.Content.ReadAsStringAsync());
            Assert.Equal("machine-eink-1", version.RootElement.GetProperty("machineId").GetString());
            Assert.Equal("package-eink-1", version.RootElement.GetProperty("package").GetProperty("packageId").GetString());

            using var conditional = Get($"/api/v1/eink/tablets/{TabletId}/version");
            conditional.Headers.IfNoneMatch.Add(versionResponse.Headers.ETag!);
            using var unchanged = await client.SendAsync(conditional);
            Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);

            using var screenResponse = await client.SendAsync(Get(
                $"/api/v1/eink/tablets/{TabletId}/machine-screen"));
            using var screen = JsonDocument.Parse(await screenResponse.Content.ReadAsStringAsync());
            Assert.Equal("M-EINK-1", screen.RootElement.GetProperty("machine").GetProperty("number").GetString());
            Assert.Equal("operation-eink-1", screen.RootElement.GetProperty("current").GetProperty("batchOperationId").GetString());
            Assert.Equal(3, screen.RootElement.GetProperty("next").GetArrayLength());
            Assert.Equal("current", screen.RootElement.GetProperty("status").GetProperty("code").GetString());

            using var manifestResponse = await client.SendAsync(Get(
                $"/api/v1/eink/tablets/{TabletId}/package-manifest"));
            Assert.Equal(
                $"/api/v1/eink/tablets/{TabletId}/packages/package-eink-1/revisions/R1/manifest",
                manifestResponse.Content.Headers.ContentLocation?.OriginalString);
            using var manifest = JsonDocument.Parse(await manifestResponse.Content.ReadAsStringAsync());
            var file = Assert.Single(manifest.RootElement.GetProperty("files").EnumerateArray());
            Assert.Equal("instructions/setup.txt", file.GetProperty("logicalPath").GetString());
            Assert.False(file.TryGetProperty("storageRelativePath", out _));
            Assert.False(file.TryGetProperty("fullPath", out _));

            var downloadPath = file.GetProperty("downloadPath").GetString()!;
            using var fileResponse = await client.SendAsync(Get(downloadPath));
            Assert.Equal(HttpStatusCode.OK, fileResponse.StatusCode);
            Assert.Equal(fileBytes, await fileResponse.Content.ReadAsByteArrayAsync());
            Assert.Equal(
                Sha256(fileBytes),
                fileResponse.Headers.GetValues("X-Meimad-Checksum-SHA256").Single());

            using var timeResponse = await client.SendAsync(Get(
                $"/api/v1/eink/tablets/{TabletId}/time-config"));
            using var time = JsonDocument.Parse(await timeResponse.Content.ReadAsStringAsync());
            Assert.Equal("Asia/Jerusalem", time.RootElement.GetProperty("timeZoneId").GetString());
            Assert.Equal(300, time.RootElement.GetProperty("pollIntervalSeconds").GetInt32());
        });
    }

    [Fact]
    public async Task Tablet_reads_use_tablet_id_without_credentials_and_disabled_tablets_are_rejected()
    {
        await RunWithServerAsync(async (application, client, packageRoot) =>
        {
            await SeedAsync(application.Services, packageRoot);

            using var noCredential = Get($"/api/v1/eink/tablets/{TabletId}/version");
            using var noCredentialResponse = await client.SendAsync(noCredential);
            Assert.Equal(HttpStatusCode.OK, noCredentialResponse.StatusCode);

            using var otherDevice = Get("/api/v1/eink/tablets/9999/version");
            using var otherResponse = await client.SendAsync(otherDevice);
            Assert.Equal(HttpStatusCode.NotFound, otherResponse.StatusCode);

            await SetDeviceEnabledAsync(application.Services, false);
            using var revokedResponse = await client.SendAsync(Get(
                $"/api/v1/eink/tablets/{TabletId}/machine-screen"));
            Assert.Equal(HttpStatusCode.NotFound, revokedResponse.StatusCode);
        });
    }

    [Fact]
    public async Task Tablet_bootstrap_uses_mac_only_for_enabled_device_discovery()
    {
        await RunWithServerAsync(async (application, client, packageRoot) =>
        {
            await SeedAsync(application.Services, packageRoot);
            using var valid = Get("/api/tablet/ping?hardwareId=a4-cf-12-83-76-91");
            valid.Headers.Add("X-Meimad-Battery-Voltage", "3.860");
            using var response = await client.SendAsync(valid);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("3041", document.RootElement.GetProperty("tabletId").GetString());
            Assert.Equal(2, document.RootElement.EnumerateObject().Count());

            using var wrongHardware = await client.SendAsync(Get(
                "/api/tablet/ping?hardwareId=A4:CF:12:83:76:99"));
            Assert.Equal(HttpStatusCode.NotFound, wrongHardware.StatusCode);

            await SetDeviceEnabledAsync(application.Services, false);
            using var revoked = await client.SendAsync(Get(
                "/api/tablet/ping?hardwareId=A4:CF:12:83:76:91"));
            Assert.Equal(HttpStatusCode.NotFound, revoked.StatusCode);
        });
    }

    [Fact]
    public async Task Terminal_monitoring_is_read_only_and_projects_device_run_workflow_and_package()
    {
        await RunWithServerAsync(async (application, client, packageRoot) =>
        {
            await SeedAsync(application.Services, packageRoot);
            await ExecuteAsync(application.Services, """
                INSERT INTO eink_package_revisions(
                    id,batch_operation_id,revision,published_at)
                VALUES('package-eink-2','operation-eink-1','R2','2099-01-01T00:00:00Z');
                """);
            using var ping = Get("/api/tablet/ping?hardwareId=A4:CF:12:83:76:91");
            ping.Headers.Add("X-Meimad-Battery-Voltage", "3.860");
            ping.Headers.Add("X-Meimad-Battery-Percent", "72");
            ping.Headers.Add("X-Meimad-Firmware-Version", "0.1.0-test");
            ping.Headers.Add("X-Meimad-Wifi-IP", "192.168.50.31");
            ping.Headers.Add("X-Meimad-Wifi-Rssi", "-61");
            using var pingResponse = await client.SendAsync(ping);
            Assert.Equal(HttpStatusCode.OK, pingResponse.StatusCode);

            // Monitoring deliberately needs no Edit Mode headers.
            using var response = await client.GetAsync("/api/v1/eink/device-registrations");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(json);
            var terminal = document.RootElement.GetProperty("items")
                .EnumerateArray().Single(item =>
                    item.GetProperty("deviceId").GetString() == DeviceId);
            Assert.Equal("3041", terminal.GetProperty("tabletId").GetString());
            Assert.Equal("A4:CF:12:83:76:91", terminal.GetProperty("hardwareId").GetString());
            Assert.Equal("M-EINK-1", terminal.GetProperty("machineNumber").GetString());
            Assert.Equal("E-Ink Mill", terminal.GetProperty("machineName").GetString());
            Assert.Equal("0.1.0-test", terminal.GetProperty("firmwareVersion").GetString());
            Assert.Equal(3.860m, terminal.GetProperty("batteryVoltage").GetDecimal());
            Assert.Equal(72, terminal.GetProperty("batteryPercent").GetInt32());
            Assert.Equal("192.168.50.31", terminal.GetProperty("wifiIpAddress").GetString());
            Assert.Equal(-61, terminal.GetProperty("wifiRssi").GetInt32());
            Assert.Equal("run:batch-operation:operation-eink-1",
                terminal.GetProperty("currentProductionRunId").GetString());
            Assert.Equal("READY_FOR_SETUP",
                terminal.GetProperty("currentWorkflowStatus").GetString());
            Assert.Equal("R2", terminal.GetProperty("currentPackageRevision").GetString());
            Assert.True(terminal.GetProperty("lastSeenAt").GetDateTimeOffset() > DateTimeOffset.MinValue);
            Assert.DoesNotContain("credentialHash", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("registrationToken", json, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Physical_tablet_status_is_scoped_stable_and_tracks_authoritative_run_state()
    {
        await RunWithServerAsync(async (application, client, packageRoot) =>
        {
            await SeedAsync(application.Services, packageRoot);

            using var firstResponse = await client.SendAsync(Get("/api/tablets/3041/status"));
            Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
            using var first = JsonDocument.Parse(await firstResponse.Content.ReadAsStringAsync());
            var firstRoot = first.RootElement;
            var firstRevision = firstRoot.GetProperty("revision").GetUInt32();
            Assert.Equal("3041", firstRoot.GetProperty("tablet_id").GetString());
            Assert.Equal("machine-eink-1", firstRoot.GetProperty("machine").GetProperty("id").GetString());
            Assert.Equal("M-EINK-1", firstRoot.GetProperty("machine").GetProperty("number").GetString());
            Assert.Equal("run:batch-operation:operation-eink-1",
                firstRoot.GetProperty("nc_run").GetProperty("id").GetString());
            Assert.Equal("PN-EINK", firstRoot.GetProperty("part").GetProperty("number").GetString());
            Assert.Equal(10, firstRoot.GetProperty("operation").GetProperty("number").GetInt32());
            Assert.Equal("READY_FOR_SETUP", firstRoot.GetProperty("status").GetString());

            using var unchangedResponse = await client.SendAsync(Get("/api/tablets/3041/status"));
            using var unchanged = JsonDocument.Parse(await unchangedResponse.Content.ReadAsStringAsync());
            Assert.Equal(firstRevision, unchanged.RootElement.GetProperty("revision").GetUInt32());

            Assert.NotNull(await ReadDeviceContactAsync(application.Services));

            await ExecuteAsync(application.Services, """
                UPDATE production_runs
                SET status = 'IN_PROGRESS', version = version + 1
                WHERE id = 'run:batch-operation:operation-eink-1';
                UPDATE production_run_programs
                SET status = 'ACTIVE', version = version + 1
                WHERE production_run_id = 'run:batch-operation:operation-eink-1';
                INSERT INTO production_run_workflow_events (
                    id, production_run_id, machine_id, event_type, source,
                    source_event_id, server_received_at, machine_timestamp)
                VALUES (
                    'workflow-setup-eink-1', 'run:batch-operation:operation-eink-1',
                    'machine-eink-1', 'SETUP_VERIFICATION_SUCCEEDED', 'TEST',
                    'setup-eink-1', '2026-08-25T10:10:00Z', '2026-08-25T10:09:59Z');
                """);
            using var setupResponse = await client.SendAsync(Get("/api/tablets/3041/status"));
            using var setup = JsonDocument.Parse(await setupResponse.Content.ReadAsStringAsync());
            Assert.Equal("IN_SETUP_RUN", setup.RootElement.GetProperty("status").GetString());
            Assert.NotEqual(firstRevision, setup.RootElement.GetProperty("revision").GetUInt32());

            await ExecuteAsync(application.Services, """
                UPDATE production_run_programs
                SET completed_cycle_count = 1, version = version + 1
                WHERE production_run_id = 'run:batch-operation:operation-eink-1';
                INSERT INTO production_run_workflow_events (
                    id, production_run_id, machine_id, event_type, source,
                    source_event_id, server_received_at, machine_timestamp)
                VALUES (
                    'workflow-cycle-eink-1', 'run:batch-operation:operation-eink-1',
                    'machine-eink-1', 'CYCLE_START', 'TEST',
                    'cycle-eink-1', '2026-08-25T10:12:00Z', '2026-08-25T10:11:59Z');
                """);
            using var productionResponse = await client.SendAsync(Get("/api/tablets/3041/status"));
            using var production = JsonDocument.Parse(await productionResponse.Content.ReadAsStringAsync());
            Assert.Equal("IN_PRODUCTION", production.RootElement.GetProperty("status").GetString());

            await ExecuteAsync(application.Services, """
                INSERT INTO production_run_workflow_events (
                    id, tablet_device_id, machine_id, production_run_id, event_type,
                    source, source_event_id, machine_timestamp, server_received_at)
                VALUES (
                    'tablet-event-eink-1', 'device-eink-1', 'machine-eink-1',
                    'run:batch-operation:operation-eink-1', 'SEND_TO_QC', 'TABLET',
                    'tablet-event-eink-1', '2026-08-25T10:15:30Z', '2026-08-25T10:15:30Z');
                """);
            using var qcResponse = await client.SendAsync(Get("/api/tablets/3041/status"));
            using var qc = JsonDocument.Parse(await qcResponse.Content.ReadAsStringAsync());
            Assert.Equal("IN_QC", qc.RootElement.GetProperty("status").GetString());

            using var wrongPath = await client.SendAsync(Get("/api/tablets/3042/status"));
            Assert.Equal(HttpStatusCode.NotFound, wrongPath.StatusCode);
        });
    }

    [Fact]
    public async Task Send_to_qc_resolves_current_run_is_idempotent_and_changes_only_workflow_projection()
    {
        await RunWithServerAsync(async (application, client, packageRoot) =>
        {
            await SeedAsync(application.Services, packageRoot);
            await EnterSetupRunAsync(application.Services);
            var before = await ReadPlanningFingerprintAsync(application.Services);

            using var firstResponse = await client.SendAsync(PostEvent("3041",
                new { event_type = "SEND_TO_QC" }));
            Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
            using var first = JsonDocument.Parse(await firstResponse.Content.ReadAsStringAsync());
            Assert.Equal("3041", first.RootElement.GetProperty("tablet_id").GetString());
            Assert.Equal("SEND_TO_QC", first.RootElement.GetProperty("event_type").GetString());
            Assert.False(first.RootElement.GetProperty("duplicate").GetBoolean());
            var acceptedAt = first.RootElement.GetProperty("timestamp").GetDateTimeOffset();

            using var retryResponse = await client.SendAsync(PostEvent("3041",
                new { event_type = "SEND_TO_QC" }));
            Assert.Equal(HttpStatusCode.OK, retryResponse.StatusCode);
            using var retry = JsonDocument.Parse(await retryResponse.Content.ReadAsStringAsync());
            Assert.True(retry.RootElement.GetProperty("duplicate").GetBoolean());
            Assert.Equal(acceptedAt,
                retry.RootElement.GetProperty("timestamp").GetDateTimeOffset());

            Assert.Equal(1, await CountSendToQcAsync(application.Services));
            Assert.Equal(before, await ReadPlanningFingerprintAsync(application.Services));
            using var statusResponse = await client.SendAsync(Get("/api/tablets/3041/status"));
            using var status = JsonDocument.Parse(await statusResponse.Content.ReadAsStringAsync());
            Assert.Equal("IN_QC", status.RootElement.GetProperty("status").GetString());
        });
    }

    [Fact]
    public async Task Send_to_qc_rejects_wrong_scope_state_event_and_client_selected_target()
    {
        await RunWithServerAsync(async (application, client, packageRoot) =>
        {
            await SeedAsync(application.Services, packageRoot);

            using var tooEarly = await client.SendAsync(PostEvent("3041",
                new { event_type = "SEND_TO_QC" }));
            Assert.Equal(HttpStatusCode.Conflict, tooEarly.StatusCode);

            await EnterSetupRunAsync(application.Services);
            using var wrongTablet = await client.SendAsync(PostEvent("9999",
                new { event_type = "SEND_TO_QC" }));
            Assert.Equal(HttpStatusCode.NotFound, wrongTablet.StatusCode);
            using var wrongEvent = await client.SendAsync(PostEvent("3041",
                new { event_type = "QC_PASS" }));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, wrongEvent.StatusCode);
            using var suppliedTarget = await client.SendAsync(PostEvent("3041",
                new
                {
                    event_type = "SEND_TO_QC",
                    production_run_id = "run:batch-operation:operation-eink-1",
                    timestamp = "2026-08-26T00:00:00Z"
                }));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, suppliedTarget.StatusCode);
            Assert.Equal(0, await CountSendToQcAsync(application.Services));
        });
    }

    [Fact]
    public async Task Concurrent_send_to_qc_retries_create_one_event_and_return_one_timestamp()
    {
        await RunWithServerAsync(async (application, client, packageRoot) =>
        {
            await SeedAsync(application.Services, packageRoot);
            await EnterSetupRunAsync(application.Services);

            var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
            {
                using var response = await client.SendAsync(PostEvent("3041",
                    new { event_type = "SEND_TO_QC" }));
                var json = await response.Content.ReadAsStringAsync();
                return (response.StatusCode, json);
            }));

            Assert.All(responses, value => Assert.Equal(HttpStatusCode.OK, value.StatusCode));
            var timestamps = responses.Select(value =>
            {
                using var document = JsonDocument.Parse(value.json);
                return document.RootElement.GetProperty("timestamp").GetDateTimeOffset();
            }).Distinct().ToArray();
            Assert.Single(timestamps);
            Assert.Equal(1, responses.Count(value =>
            {
                using var document = JsonDocument.Parse(value.json);
                return !document.RootElement.GetProperty("duplicate").GetBoolean();
            }));
            Assert.Equal(1, await CountSendToQcAsync(application.Services));
        });
    }

    [Fact]
    public async Task Qc_queue_supports_fail_resend_and_pass_with_user_reason_and_approval_time()
    {
        await RunWithServerAsync(async (application, client, packageRoot) =>
        {
            await SeedAsync(application.Services, packageRoot);
            await EnterSetupRunAsync(application.Services);
            var planningBefore = await ReadPlanningFingerprintAsync(application.Services);
            using (var send = await client.SendAsync(PostEvent(
                "3041", new { event_type = "SEND_TO_QC" })))
                Assert.Equal(HttpStatusCode.OK, send.StatusCode);

            using (var queueResponse = await client.GetAsync("/api/v1/qc-queue"))
            {
                Assert.Equal(HttpStatusCode.OK, queueResponse.StatusCode);
                using var queue = JsonDocument.Parse(await queueResponse.Content.ReadAsStringAsync());
                var item = Assert.Single(queue.RootElement.GetProperty("items").EnumerateArray());
                Assert.Equal("M-EINK-1", item.GetProperty("machineNumber").GetString());
                Assert.Equal("PN-EINK", item.GetProperty("part").GetString());
                Assert.Equal("OP10 Rough", item.GetProperty("operation").GetString());
                Assert.Equal("run:batch-operation:operation-eink-1",
                    item.GetProperty("productionRunId").GetString());
                Assert.Equal("Setup Worker", item.GetProperty("setupistName").GetString());
                Assert.True(item.GetProperty("receivedAt").GetDateTimeOffset()
                    > DateTimeOffset.MinValue);
            }

            using (var unauthorized = await client.SendAsync(PostQcDecision(
                       "PASS", "not authorized", includeAuthority: false)))
                Assert.Equal((HttpStatusCode)428, unauthorized.StatusCode);

            await GrantEditAsync(application.Services);
            using (var failedResponse = await client.SendAsync(PostQcDecision(
                       "FAIL", "Surface finish outside limit")))
            {
                Assert.Equal(HttpStatusCode.OK, failedResponse.StatusCode);
                using var failed = JsonDocument.Parse(await failedResponse.Content.ReadAsStringAsync());
                Assert.Equal("IN_SETUP_RUN", failed.RootElement.GetProperty("resultingStatus").GetString());
                Assert.Equal(JsonValueKind.Null,
                    failed.RootElement.GetProperty("productionApprovedAt").ValueKind);
            }

            using (var statusResponse = await client.SendAsync(Get("/api/tablets/3041/status")))
            {
                using var status = JsonDocument.Parse(await statusResponse.Content.ReadAsStringAsync());
                Assert.Equal("IN_SETUP_RUN", status.RootElement.GetProperty("status").GetString());
            }
            Assert.Empty((await ReadQueueAsync(client)).EnumerateArray());

            using (var resendResponse = await client.SendAsync(PostEvent(
                "3041", new { event_type = "SEND_TO_QC" })))
            {
                Assert.Equal(HttpStatusCode.OK, resendResponse.StatusCode);
                using var resend = JsonDocument.Parse(await resendResponse.Content.ReadAsStringAsync());
                Assert.False(resend.RootElement.GetProperty("duplicate").GetBoolean());
            }
            Assert.Equal(2, await CountSendToQcAsync(application.Services));
            Assert.Single((await ReadQueueAsync(client)).EnumerateArray());

            DateTimeOffset approvedAt;
            using (var passedResponse = await client.SendAsync(PostQcDecision(
                       "PASS", "First article accepted")))
            {
                Assert.Equal(HttpStatusCode.OK, passedResponse.StatusCode);
                using var passed = JsonDocument.Parse(await passedResponse.Content.ReadAsStringAsync());
                Assert.Equal("READY_FOR_PRODUCTION",
                    passed.RootElement.GetProperty("resultingStatus").GetString());
                approvedAt = passed.RootElement.GetProperty("productionApprovedAt").GetDateTimeOffset();
                Assert.Equal(
                    passed.RootElement.GetProperty("timestamp").GetDateTimeOffset(), approvedAt);
            }
            Assert.Empty((await ReadQueueAsync(client)).EnumerateArray());
            using (var statusResponse = await client.SendAsync(Get("/api/tablets/3041/status")))
            {
                using var status = JsonDocument.Parse(await statusResponse.Content.ReadAsStringAsync());
                Assert.Equal("READY_FOR_PRODUCTION", status.RootElement.GetProperty("status").GetString());
            }
            using (var duplicateDecision = await client.SendAsync(PostQcDecision("PASS", null)))
                Assert.Equal(HttpStatusCode.Conflict, duplicateDecision.StatusCode);

            Assert.Equal(planningBefore, await ReadPlanningFingerprintAsync(application.Services));
            var audit = await ReadQcAuditAsync(application.Services);
            Assert.Equal(2, audit.Count);
            Assert.All(audit, value => Assert.Equal("eink-admin-user", value.UserId));
            Assert.Equal("Surface finish outside limit", audit[0].Reason);
            Assert.Equal("First article accepted", audit[1].Reason);
            Assert.Equal(approvedAt, audit[1].Timestamp);
        });
    }

    [Fact]
    public async Task Qc_decisions_reject_wrong_state_scope_authority_and_payload()
    {
        await RunWithServerAsync(async (application, client, packageRoot) =>
        {
            await SeedAsync(application.Services, packageRoot);
            await GrantEditAsync(application.Services);

            using (var premature = await client.SendAsync(PostQcDecision("PASS", null)))
                Assert.Equal(HttpStatusCode.Conflict, premature.StatusCode);

            await EnterSetupRunAsync(application.Services);
            using (var send = await client.SendAsync(PostEvent(
                       "3041", new { event_type = "SEND_TO_QC" })))
                Assert.Equal(HttpStatusCode.OK, send.StatusCode);

            using (var invalidDecision = await client.SendAsync(
                       PostQcDecision("HOLD", null)))
                Assert.Equal(HttpStatusCode.UnprocessableEntity, invalidDecision.StatusCode);
            using (var longReason = await client.SendAsync(
                       PostQcDecision("FAIL", new string('x', 1001))))
                Assert.Equal(HttpStatusCode.UnprocessableEntity, longReason.StatusCode);
            using (var staleAuthority = await client.SendAsync(
                       PostQcDecision("PASS", null, generation: "2")))
                Assert.Equal(HttpStatusCode.Conflict, staleAuthority.StatusCode);
            using (var wrongUser = await client.SendAsync(
                       PostQcDecision("PASS", null, userId: "other-user")))
                Assert.Equal(HttpStatusCode.Conflict, wrongUser.StatusCode);
            using (var unknownRun = await client.SendAsync(PostQcDecision(
                       "PASS", null, productionRunId: "unknown-run")))
                Assert.Equal(HttpStatusCode.NotFound, unknownRun.StatusCode);
            Assert.Single((await ReadQueueAsync(client)).EnumerateArray());
            Assert.Empty(await ReadQcAuditAsync(application.Services));
        });
    }

    [Fact]
    public async Task Concurrent_qc_decisions_append_exactly_one_result()
    {
        await RunWithServerAsync(async (application, client, packageRoot) =>
        {
            await SeedAsync(application.Services, packageRoot);
            await GrantEditAsync(application.Services);
            await EnterSetupRunAsync(application.Services);
            using (var send = await client.SendAsync(PostEvent(
                       "3041", new { event_type = "SEND_TO_QC" })))
                Assert.Equal(HttpStatusCode.OK, send.StatusCode);

            var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(async index =>
            {
                using var response = await client.SendAsync(PostQcDecision(
                    "PASS", $"Concurrent decision {index}"));
                return response.StatusCode;
            }));

            Assert.Single(responses, value => value == HttpStatusCode.OK);
            Assert.Single(responses, value => value == HttpStatusCode.Conflict);
            var audit = await ReadQcAuditAsync(application.Services);
            Assert.Single(audit);
            Assert.Empty((await ReadQueueAsync(client)).EnumerateArray());
        });
    }

    [Fact]
    public async Task Tablet_status_exposes_only_the_derived_code_for_a_valid_pending_session()
    {
        await RunWithServerAsync(async (application, client, packageRoot) =>
        {
            await SeedAsync(application.Services, packageRoot);
            var now = application.Services.GetRequiredService<TimeProvider>().GetUtcNow();
            await SeedVerificationSessionAsync(application.Services, now, now.AddMinutes(5));

            using var response = await client.SendAsync(Get("/api/tablets/3041/status"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            Assert.Equal("IN_SETUP", root.GetProperty("status").GetString());
            var verification = root.GetProperty("verification");
            Assert.True(verification.GetProperty("required").GetBoolean());
            Assert.Equal("WAITING_FOR_OPERATOR", verification.GetProperty("state").GetString());
            Assert.Equal("0388", verification.GetProperty("response_code").GetString());
            var diagnostics = root.GetProperty("diagnostics");
            Assert.Equal(
                "WAITING_FOR_OPERATOR",
                diagnostics.GetProperty("verification_result").GetString());
            Assert.Equal(3, diagnostics.GetProperty("protected_macro_version").GetInt32());
            Assert.DoesNotContain("100000", json, StringComparison.Ordinal);
            Assert.DoesNotContain("699624", json, StringComparison.Ordinal);
            Assert.DoesNotContain("tablet-verification-secret", json, StringComparison.Ordinal);
            Assert.DoesNotContain("protectedSecret", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("nonce", json, StringComparison.OrdinalIgnoreCase);

            await ExecuteAsync(application.Services, """
                UPDATE production_run_programs
                SET selected_gcode_release_id=NULL
                WHERE production_run_id='run:batch-operation:operation-eink-1';
                """);
            using var wrongNcResponse = await client.SendAsync(Get("/api/tablets/3041/status"));
            using var wrongNc = JsonDocument.Parse(await wrongNcResponse.Content.ReadAsStringAsync());
            var wrongNcVerification = wrongNc.RootElement.GetProperty("verification");
            Assert.Equal("INVALIDATED", wrongNcVerification.GetProperty("state").GetString());
            Assert.False(wrongNcVerification.TryGetProperty("response_code", out _));

            await ExecuteAsync(application.Services, """
                UPDATE production_run_programs
                SET selected_gcode_release_id='gcode-eink-verification'
                WHERE production_run_id='run:batch-operation:operation-eink-1';
                """);
            await ExecuteAsync(application.Services,
                "UPDATE cnc_verification_settings SET enabled=0 WHERE machine_id='machine-eink-1';");
            using var disabledResponse = await client.SendAsync(Get("/api/tablets/3041/status"));
            using var disabled = JsonDocument.Parse(await disabledResponse.Content.ReadAsStringAsync());
            var disabledVerification = disabled.RootElement.GetProperty("verification");
            Assert.Equal("INVALIDATED", disabledVerification.GetProperty("state").GetString());
            Assert.False(disabledVerification.TryGetProperty("response_code", out _));

            await ExecuteAsync(application.Services, $"""
                UPDATE cnc_verification_settings
                SET enabled=1 WHERE machine_id='machine-eink-1';
                UPDATE cnc_setup_verification_sessions
                SET state='SUPERSEDED', resolved_at='{now:O}'
                WHERE id='session-eink-verification';
                """);
            using var supersededResponse = await client.SendAsync(Get("/api/tablets/3041/status"));
            using var superseded = JsonDocument.Parse(
                await supersededResponse.Content.ReadAsStringAsync());
            var supersededVerification = superseded.RootElement.GetProperty("verification");
            Assert.Equal("UNAVAILABLE", supersededVerification.GetProperty("state").GetString());
            Assert.False(supersededVerification.TryGetProperty("response_code", out _));
            Assert.Equal(
                "SUPERSEDED",
                superseded.RootElement.GetProperty("diagnostics")
                    .GetProperty("verification_result").GetString());
        });
    }

    [Fact]
    public async Task Expired_verification_session_never_exposes_a_response_code()
    {
        await RunWithServerAsync(async (application, client, packageRoot) =>
        {
            await SeedAsync(application.Services, packageRoot);
            var now = application.Services.GetRequiredService<TimeProvider>().GetUtcNow();
            await SeedVerificationSessionAsync(
                application.Services, now.AddMinutes(-10), now.AddSeconds(-1));

            using var response = await client.SendAsync(Get("/api/tablets/3041/status"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var verification = document.RootElement.GetProperty("verification");
            Assert.Equal("EXPIRED", verification.GetProperty("state").GetString());
            Assert.False(verification.TryGetProperty("response_code", out _));
        });
    }

    [Fact]
    public async Task Corrupt_package_file_is_rejected_without_returning_bytes()
    {
        await RunWithServerAsync(async (application, client, packageRoot) =>
        {
            await SeedAsync(application.Services, packageRoot);
            await File.WriteAllTextAsync(
                Path.Combine(packageRoot, "package-eink-1", "setup.txt"),
                "corrupted package content");

            using var response = await client.SendAsync(Get(
                $"/api/v1/eink/tablets/{TabletId}/packages/package-eink-1/revisions/R1/files/file-eink-1"));
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("package_integrity_failed", document.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.DoesNotContain("corrupted package content", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Active_editor_can_register_bind_disable_and_enable_a_tablet_without_credentials()
    {
        await RunWithServerAsync(async (application, client, packageRoot) =>
        {
            await SeedAsync(application.Services, packageRoot);
            await GrantEditAsync(application.Services);
            client.DefaultRequestHeaders.Add("X-Meimad-Client-Id", "eink-admin-client");
            client.DefaultRequestHeaders.Add("X-Meimad-Edit-Generation", "1");

            using var unidentified = await client.PostAsJsonAsync(
                "/api/v1/eink/device-registrations",
                new { deviceName = "Unknown spare", machineId = (string?)null });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, unidentified.StatusCode);

            using var create = await client.PostAsJsonAsync(
                "/api/v1/eink/device-registrations",
                new
                {
                    deviceName = "Spare Tablet",
                    machineId = (string?)null,
                    hardwareId = "A4:CF:12:83:76:93"
                });
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
            using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
            var deviceId = created.RootElement.GetProperty("deviceId").GetString()!;
            var tabletId = created.RootElement.GetProperty("tabletId").GetString()!;
            Assert.False(created.RootElement.TryGetProperty("registrationToken", out _));

            await UnbindDeviceAsync(application.Services, DeviceId);
            using var updateRequest = new HttpRequestMessage(
                HttpMethod.Patch,
                $"/api/v1/eink/device-registrations/{deviceId}")
            {
                Content = JsonContent.Create(new
                {
                    machineId = "machine-eink-1",
                    isEnabled = true
                })
            };
            using var update = await client.SendAsync(updateRequest);
            Assert.Equal(HttpStatusCode.OK, update.StatusCode);
            using var updated = JsonDocument.Parse(await update.Content.ReadAsStringAsync());
            Assert.False(updated.RootElement.TryGetProperty("registrationToken", out _));
            using var enabledRead = await client.SendAsync(Get(
                $"/api/v1/eink/tablets/{tabletId}/version"));
            Assert.Equal(HttpStatusCode.OK, enabledRead.StatusCode);

            using var revokeRequest = new HttpRequestMessage(
                HttpMethod.Patch,
                $"/api/v1/eink/device-registrations/{deviceId}")
            {
                Content = JsonContent.Create(new
                {
                    machineId = "machine-eink-1",
                    isEnabled = false
                })
            };
            using var revoke = await client.SendAsync(revokeRequest);
            Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
            using var revoked = await client.SendAsync(Get(
                $"/api/v1/eink/tablets/{tabletId}/version"));
            Assert.Equal(HttpStatusCode.NotFound, revoked.StatusCode);

            var audit = await ReadTabletAdministrationAuditAsync(
                application.Services, deviceId);
            Assert.Equal(1, audit.Registered);
            Assert.Equal(2, audit.RecoveryActions);
            Assert.Equal(1, audit.Disables);
            Assert.Equal(3, audit.AttributedToEditor);
        });
    }

    [Fact]
    public async Task Simulator_matches_monochrome_firmware_layout_and_physical_button_contract()
    {
        await RunWithServerAsync(async (_, client, _) =>
        {
            using var page = await client.GetAsync("/eink-simulator/");
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            var html = await page.Content.ReadAsStringAsync();
            Assert.Contains("800 x 480 monochrome", html, StringComparison.Ordinal);
            Assert.Contains("UC8179", html, StringComparison.Ordinal);
            Assert.Contains("id=\"hardware-id\"", html, StringComparison.Ordinal);
            Assert.Contains("SEND_TO_QC", html, StringComparison.Ordinal);
            Assert.Contains("id=\"eink-screen\"", html, StringComparison.Ordinal);
            Assert.Contains("id=\"panel-canvas\"", html, StringComparison.Ordinal);
            Assert.Contains("width=\"800\" height=\"480\"", html, StringComparison.Ordinal);
            Assert.Contains("id=\"production-screen\"", html, StringComparison.Ordinal);
            Assert.Contains("id=\"service-screen\"", html, StringComparison.Ordinal);
            Assert.Contains("id=\"button-d1\"", html, StringComparison.Ordinal);
            Assert.Contains("id=\"button-d2\"", html, StringComparison.Ordinal);
            Assert.Contains("id=\"button-d4\"", html, StringComparison.Ordinal);
            Assert.Contains("id=\"button-reset\"", html, StringComparison.Ordinal);
            Assert.Contains("PREVIOUS TOOL PAGE", html, StringComparison.Ordinal);
            Assert.Contains("NEXT TOOL PAGE", html, StringComparison.Ordinal);
            Assert.Contains("HOLD: SERVICE / DEBUG", html, StringComparison.Ordinal);
            Assert.Contains("HOLD: SEND_TO_QC", html, StringComparison.Ordinal);
            Assert.Contains("SETUP VERIFICATION", html, StringComparison.Ordinal);
            Assert.Contains("NO TOOL DATA AVAILABLE", html, StringComparison.Ordinal);
            foreach (var status in new[] { "READY_FOR_SETUP", "IN_SETUP", "IN_SETUP_RUN", "IN_QC", "READY_FOR_PRODUCTION", "IN_PRODUCTION", "BLOCKED" })
            {
                Assert.Contains(status, html, StringComparison.Ordinal);
            }
            Assert.Contains("Server offline", html, StringComparison.Ordinal);
            Assert.Contains("Low battery", html, StringComparison.Ordinal);
            Assert.DoesNotContain("textarea", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Color E-Ink Work Tablet Simulator", html, StringComparison.Ordinal);
            Assert.DoesNotContain("data-page=", html, StringComparison.Ordinal);

            using var styles = await client.GetAsync("/eink-simulator/styles.css");
            var css = await styles.Content.ReadAsStringAsync();
            Assert.Contains("aspect-ratio: 5 / 3", css, StringComparison.Ordinal);
            Assert.Contains("filter: grayscale(1)", css, StringComparison.Ordinal);
            Assert.Contains("background: var(--paper)", css, StringComparison.Ordinal);
            Assert.Contains("image-rendering: pixelated", css, StringComparison.Ordinal);
            Assert.Contains(".screen-view { display: none", css, StringComparison.Ordinal);
            Assert.DoesNotContain("--blue:", css, StringComparison.Ordinal);
            Assert.DoesNotContain("linear-gradient", css, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("font-family: Arial", css, StringComparison.OrdinalIgnoreCase);

            using var script = await client.GetAsync("/eink-simulator/app.js");
            var javascript = await script.Content.ReadAsStringAsync();
            Assert.Contains("const HOLD_MILLISECONDS = 1200", javascript, StringComparison.Ordinal);
            Assert.Contains("const GLCD_FONT = new Uint8Array", javascript, StringComparison.Ordinal);
            Assert.Contains("function drawBitmapText", javascript, StringComparison.Ordinal);
            Assert.Contains("drawProductionCanvas(model)", javascript, StringComparison.Ordinal);
            Assert.Contains("drawBitmapText(context, fitBitmapText(machine, 610, 4), left, 18, 4)", javascript, StringComparison.Ordinal);
            Assert.Contains("/api/tablet/ping?hardwareId=", javascript, StringComparison.Ordinal);
            Assert.Contains("method: \"POST\"", javascript, StringComparison.Ordinal);
            Assert.Contains("{ event_type: \"SEND_TO_QC\" }", javascript, StringComparison.Ordinal);
            Assert.Contains("requestStatus(\"before-SEND_TO_QC\")", javascript, StringComparison.Ordinal);
            Assert.Contains("requestStatus(\"after-SEND_TO_QC\")", javascript, StringComparison.Ordinal);
            Assert.Contains("current?.status !== \"IN_SETUP_RUN\"", javascript, StringComparison.Ordinal);
            Assert.Contains("verification?.state === \"WAITING_FOR_OPERATOR\"", javascript, StringComparison.Ordinal);
            Assert.Contains("renderProduction(makeUnavailableModel())", javascript, StringComparison.Ordinal);
            Assert.Contains("changeToolPage(-1)", javascript, StringComparison.Ordinal);
            Assert.Contains("changeToolPage(1)", javascript, StringComparison.Ordinal);
            Assert.Contains("showServiceScreen", javascript, StringComparison.Ordinal);
            Assert.Contains("LOW BATTERY", html, StringComparison.Ordinal);
            Assert.DoesNotContain("/api/v1/eink/tablets/", javascript, StringComparison.Ordinal);
            Assert.DoesNotContain("crypto.subtle.digest", javascript, StringComparison.Ordinal);
            Assert.DoesNotContain("production_run_id", javascript, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("machine_id", javascript, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("edit-mode", javascript, StringComparison.OrdinalIgnoreCase);

            using var usb = await client.SendAsync(Get(
                $"/api/v1/eink/tablets/{TabletId}/usb-mass-storage"));
            Assert.Equal(HttpStatusCode.NotFound, usb.StatusCode);
        });
    }

    private static HttpRequestMessage Get(string path) => new(HttpMethod.Get, path);

    private static HttpRequestMessage PostEvent(
        string tabletId,
        object body)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/tablets/{tabletId}/events")
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add("X-Meimad-Battery-Voltage", "3.850");
        return request;
    }

    private static HttpRequestMessage PostQcDecision(
        string decision,
        string? reason,
        bool includeAuthority = true,
        string productionRunId = "run:batch-operation:operation-eink-1",
        string generation = "1",
        string userId = "eink-admin-user")
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/qc-queue/{Uri.EscapeDataString(productionRunId)}/decision")
        {
            Content = JsonContent.Create(new { decision, reason })
        };
        if (includeAuthority)
        {
            request.Headers.Add("X-Meimad-Client-Id", "eink-admin-client");
            request.Headers.Add("X-Meimad-User-Id", userId);
            request.Headers.Add("X-Meimad-Edit-Generation", generation);
        }
        return request;
    }

    private static async Task<JsonElement> ReadQueueAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/v1/qc-queue");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("items").Clone();
    }

    private static Task EnterSetupRunAsync(IServiceProvider services) => ExecuteAsync(
        services,
        """
        UPDATE production_runs
        SET status='IN_PROGRESS',version=version+1
        WHERE id='run:batch-operation:operation-eink-1';
        UPDATE production_run_programs
        SET status='ACTIVE',version=version+1
        WHERE production_run_id='run:batch-operation:operation-eink-1';
        INSERT INTO production_run_workflow_events(
            id,production_run_id,machine_id,event_type,source,source_event_id,
            server_received_at,metadata_json)
        VALUES('workflow-setup-ready','run:batch-operation:operation-eink-1',
               'machine-eink-1','SETUP_VERIFICATION_SUCCEEDED','TEST',
               'setup-ready','2026-08-26T08:00:00Z','{}');
        """);

    private static async Task<int> CountSendToQcAsync(IServiceProvider services)
    {
        var database = services.GetRequiredService<SqliteDatabase>();
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM production_run_workflow_events
            WHERE production_run_id='run:batch-operation:operation-eink-1'
              AND event_type='SEND_TO_QC';
            """;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<TabletAdministrationAudit> ReadTabletAdministrationAuditAsync(
        IServiceProvider services,
        string deviceId)
    {
        var database = services.GetRequiredService<SqliteDatabase>();
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
              SUM(CASE WHEN event_type='tablet_registered' THEN 1 ELSE 0 END),
              SUM(CASE WHEN event_type='tablet_administration_recovery' THEN 1 ELSE 0 END),
              SUM(CASE WHEN event_type='tablet_administration_recovery'
                        AND json_extract(after_data_json,'$.isEnabled')=0
                       THEN 1 ELSE 0 END),
              SUM(CASE WHEN user_id='eink-admin-user' THEN 1 ELSE 0 END)
            FROM structured_event_log
            WHERE json_extract(related_entity_ids_json,'$.tabletDeviceId')=$deviceId
              AND event_type IN('tablet_registered','tablet_administration_recovery');
            """;
        command.Parameters.AddWithValue("$deviceId", deviceId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new(
            reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2),
            reader.GetInt32(3));
    }

    private static async Task<string> ReadPlanningFingerprintAsync(IServiceProvider services)
    {
        var database = services.GetRequiredService<SqliteDatabase>();
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT run.status || '|' || run.version || '|' || program.status || '|'
                   || program.version || '|' || program.completed_cycle_count || '|'
                   || assignment.backlog_position || '|' || assignment.version || '|'
                   || (SELECT COUNT(*) FROM eink_package_revisions)
            FROM production_runs run
            JOIN production_run_programs program ON program.production_run_id=run.id
            JOIN machine_assignments assignment ON assignment.production_run_id=run.id
            WHERE run.id='run:batch-operation:operation-eink-1'
            ORDER BY program.sequence_position
            LIMIT 1;
            """;
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<IReadOnlyList<QcAuditRow>> ReadQcAuditAsync(
        IServiceProvider services)
    {
        var database = services.GetRequiredService<SqliteDatabase>();
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT user_id,json_extract(metadata_json,'$.reason'),server_received_at
            FROM production_run_workflow_events
            WHERE event_type IN ('QC_FAIL','QC_PASS')
            ORDER BY server_received_at,id;
            """;
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<QcAuditRow>();
        while (await reader.ReadAsync())
            values.Add(new(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                DateTimeOffset.Parse(reader.GetString(2))));
        return values;
    }

    private sealed record QcAuditRow(
        string UserId, string? Reason, DateTimeOffset Timestamp);

    private sealed record TabletAdministrationAudit(
        int Registered,
        int RecoveryActions,
        int Disables,
        int AttributedToEditor);

    private static async Task<byte[]> SeedAsync(IServiceProvider services, string packageRoot)
    {
        var packageDirectory = Path.Combine(packageRoot, "package-eink-1");
        Directory.CreateDirectory(packageDirectory);
        var fileBytes = Encoding.UTF8.GetBytes("SETUP INSTRUCTIONS\n1. Verify fixture.\n2. Load tools.\n");
        await File.WriteAllBytesAsync(Path.Combine(packageDirectory, "setup.txt"), fileBytes);
        var now = DateTimeOffset.UtcNow;
        var start = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var calendar = JsonSerializer.Serialize(new
        {
            availability = new[] { new { startsAt = start, endsAt = start.AddDays(7) } }
        });
        var database = services.GetRequiredService<SqliteDatabase>();
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO working_calendars (id, name, time_zone_id, calendar_json)
            VALUES ('calendar-eink', 'E-Ink calendar', 'UTC', $calendar);
            INSERT INTO application_settings (key, value)
            VALUES ('timeline.setup_calendar_json', $calendar);
            INSERT INTO machines (
                id, number, name, machine_type, working_calendar_id, status,
                is_active, display_enabled)
            VALUES ('machine-eink-1', 'M-EINK-1', 'E-Ink Mill', 'mill',
                    'calendar-eink', 'active', 1, 1);
            INSERT INTO employee_resources (
                id, employee_number, name, resource_type, first_name, last_name,
                skills_json, assigned_calendar_id, is_active)
            VALUES ('resource-eink-setup', 'E-EINK-SETUP', 'Setup Worker', 'setup_worker',
                    'Setup', 'Worker', '["mill"]', 'calendar-eink', 1);
            INSERT INTO cases (id, part_number, name, working_folder_path)
            VALUES ('case-eink', 'PN-EINK', 'E-Ink Part', 'C:\Cases\PN-EINK');
            INSERT INTO production_batches (
                id, case_id, batch_number, status, planned_quantity)
            VALUES ('batch-eink', 'case-eink', 'B-EINK', 'waiting', 4);
            INSERT INTO case_operations (
                id, case_id, operation_number, route_position, name,
                required_machine_type, setup_seconds, cycle_seconds)
            VALUES
                ('case-op-eink-1', 'case-eink', 10, 0, 'Rough', 'mill', 60, 60),
                ('case-op-eink-2', 'case-eink', 20, 1, 'Finish', 'mill', 60, 60),
                ('case-op-eink-3', 'case-eink', 30, 2, 'Deburr', 'mill', 60, 60),
                ('case-op-eink-4', 'case-eink', 40, 3, 'Inspect', 'mill', 60, 60);
            INSERT INTO batch_operations (
                id, production_batch_id, source_case_operation_id,
                operation_number, route_position, name, required_machine_type,
                setup_seconds, cycle_seconds, status)
            VALUES
                ('operation-eink-1', 'batch-eink', 'case-op-eink-1', 10, 0, 'Rough', 'mill', 60, 60, 'not_started'),
                ('operation-eink-2', 'batch-eink', 'case-op-eink-2', 20, 1, 'Finish', 'mill', 60, 60, 'not_started'),
                ('operation-eink-3', 'batch-eink', 'case-op-eink-3', 30, 2, 'Deburr', 'mill', 60, 60, 'not_started'),
                ('operation-eink-4', 'batch-eink', 'case-op-eink-4', 40, 3, 'Inspect', 'mill', 60, 60, 'not_started');
            INSERT INTO machine_assignments (
                id, batch_operation_id, machine_id, backlog_position)
            VALUES
                ('assignment-eink-1', 'operation-eink-1', 'machine-eink-1', 0),
                ('assignment-eink-2', 'operation-eink-2', 'machine-eink-1', 1),
                ('assignment-eink-3', 'operation-eink-3', 'machine-eink-1', 2),
                ('assignment-eink-4', 'operation-eink-4', 'machine-eink-1', 3);
            INSERT INTO device_registry (
                id, tablet_id, hardware_id, device_type, device_name, machine_id,
                access_mode, is_enabled)
            VALUES
                ('device-eink-1', '3041', 'A4:CF:12:83:76:91', 'eink', 'Tablet One', 'machine-eink-1',
                 'read_only', 1),
                ('device-eink-2', '3042', 'A4:CF:12:83:76:92', 'eink', 'Tablet Two', NULL,
                 'read_only', 1);
            INSERT INTO eink_package_revisions (
                id, batch_operation_id, revision, tool_cart_id, published_at,
                setup_worker_id, setup_worker_first_name, setup_worker_last_name)
            VALUES ('package-eink-1', 'operation-eink-1', 'R1', 'TC-12', $publishedAt,
                    'resource-eink-setup', 'Setup', 'Worker');
            INSERT INTO eink_package_files (
                id, package_revision_id, logical_path, storage_relative_path,
                media_type, byte_length, sha256, modified_at, display_order)
            VALUES (
                'file-eink-1', 'package-eink-1', 'instructions/setup.txt',
                'package-eink-1/setup.txt', 'text/plain; charset=utf-8',
                $byteLength, $sha256, $publishedAt, 0);
            """;
        command.Parameters.AddWithValue("$calendar", calendar);
        command.Parameters.AddWithValue("$publishedAt", now.ToString("O"));
        command.Parameters.AddWithValue("$byteLength", fileBytes.LongLength);
        command.Parameters.AddWithValue("$sha256", Sha256(fileBytes));
        await command.ExecuteNonQueryAsync();
        return fileBytes;
    }

    private static async Task SetDeviceEnabledAsync(IServiceProvider services, bool enabled)
    {
        var database = services.GetRequiredService<SqliteDatabase>();
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE device_registry SET is_enabled = $enabled WHERE id = $deviceId;";
        command.Parameters.AddWithValue("$enabled", enabled);
        command.Parameters.AddWithValue("$deviceId", DeviceId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task UnbindDeviceAsync(IServiceProvider services, string deviceId)
    {
        var database = services.GetRequiredService<SqliteDatabase>();
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE device_registry SET machine_id = NULL WHERE id = $deviceId;";
        command.Parameters.AddWithValue("$deviceId", deviceId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task GrantEditAsync(IServiceProvider services)
    {
        var database = services.GetRequiredService<SqliteDatabase>();
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE edit_tokens
            SET holder_client_id = 'eink-admin-client',
                holder_user_id = 'eink-admin-user',
                generation = 1,
                acquired_at = '2026-08-11T00:00:00Z',
                updated_at = '2026-08-11T00:00:00Z'
            WHERE id = 1;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SeedVerificationSessionAsync(
        IServiceProvider services, DateTimeOffset createdAt, DateTimeOffset expiresAt)
    {
        const string secret = "tablet-verification-secret-1";
        var protector = services.GetRequiredService<IDataProtectionProvider>().CreateProtector(
            CncVerificationSecretProtection.Purpose);
        var protectedSecret = protector.Protect(secret);
        var machineKey = CncVerificationResponseAlgorithm.DeriveMachineKey("machine-eink-1", secret);
        Assert.Equal(699624, machineKey);
        Assert.Equal("0388", CncVerificationResponseAlgorithm.Calculate(
            100000, 100000, 100000, machineKey, 4));

        var database = services.GetRequiredService<SqliteDatabase>();
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO postprocessors(id,name)VALUES('post-eink-verification','Verification Post');
            INSERT INTO tool_table_releases(
                id,case_operation_id,revision_number,original_file_name,stored_relative_path,
                file_size,file_hash,released_at,released_by,release_comment,created_at,updated_at)
            VALUES('tools-eink-verification','case-op-eink-1',1,'tools.csv','tools/verification.csv',
                   1,$hash,$createdAt,'test','verification',$createdAt,$createdAt);
            INSERT INTO process_revisions(
                id,case_operation_id,revision_number,is_active,tool_table_release_id,
                created_at,created_by,change_description,version,updated_at,manufacturing_program_id)
            VALUES('process-eink-verification','case-op-eink-1',1,1,'tools-eink-verification',
                   $createdAt,'test','verification',1,$createdAt,'case-operation:case-op-eink-1');
            INSERT INTO gcode_releases(
                id,case_operation_id,process_revision_id,postprocessor_id,post_specific_revision,
                original_file_name,stored_relative_path,file_size,file_hash,released_at,released_by,
                change_scope,release_comment,tool_table_release_id,created_at,updated_at)
            VALUES('gcode-eink-verification','case-op-eink-1','process-eink-verification',
                   'post-eink-verification',1,'part.nc','gcode/part.nc',1,$hash,$createdAt,'test',
                   'LOCAL_POST_REVISION','verification','tools-eink-verification',$createdAt,$createdAt);
            INSERT INTO gcode_release_verification_hooks(
                gcode_release_id,hook_version,invocation_kind,invocation_number,
                nc_identity_token,line_number,created_at,updated_at)
            VALUES('gcode-eink-verification',1,'G65',9002,100000,3,$createdAt,$createdAt);
            UPDATE production_run_programs
            SET manufacturing_program_id='case-operation:case-op-eink-1',
                process_revision_id='process-eink-verification',
                selected_gcode_release_id='gcode-eink-verification',
                legacy_unmanaged=0,
                updated_at=$createdAt
            WHERE production_run_id='run:batch-operation:operation-eink-1';
            INSERT INTO offset_loader_releases(
                id,production_run_id,machine_id,nc_release_id,tool_table_release_id,
                verification_release_token,created_at,created_by)
            VALUES('offset-eink-verification','run:batch-operation:operation-eink-1',
                   'machine-eink-1','gcode-eink-verification','tools-eink-verification',
                   100000,$createdAt,'test');
            INSERT INTO production_run_current_offset_loaders(
                production_run_id,machine_id,offset_loader_release_id,selected_at,selected_by,version)
            VALUES('run:batch-operation:operation-eink-1','machine-eink-1',
                   'offset-eink-verification',$createdAt,'test',1);
            INSERT INTO cnc_verification_settings(
                machine_id,dprint_transport,dprint_port,challenge_program_number,
                verify_program_number,custom_gcode_alias,nonce_variable,response_variable,
                verification_state_variable,release_token_variable,protected_secret,
                expected_macro_version,response_code_digits,verification_timeout_seconds,
                enabled,version,created_at,updated_at)
            VALUES('machine-eink-1','HAAS_DPRNT_TCP',8080,9001,9002,NULL,
                   10801,10802,10803,10804,$protectedSecret,3,4,300,1,1,$createdAt,$createdAt);
            INSERT INTO production_run_workflow_events(
                id,production_run_id,machine_id,event_type,source,source_event_id,
                server_received_at,nc_release_id,offset_loader_release_id,metadata_json)
            VALUES('workflow-offset-eink-verification','run:batch-operation:operation-eink-1',
                   'machine-eink-1','OFFSET_LOADER_COMPLETED','TEST','offset-eink-verification',
                   $createdAt,'gcode-eink-verification','offset-eink-verification','{}');
            INSERT INTO cnc_setup_verification_sessions(
                id,production_run_id,machine_id,nc_release_id,offset_loader_release_id,
                nonce,macro_version,response_code_digits,state,created_at,expires_at,
                source_workflow_event_id)
            VALUES('session-eink-verification','run:batch-operation:operation-eink-1',
                   'machine-eink-1','gcode-eink-verification','offset-eink-verification',
                   100000,3,4,'PENDING',$createdAt,$expiresAt,'workflow-offset-eink-verification');
            """;
        command.Parameters.AddWithValue("$hash", new string('d', 64));
        command.Parameters.AddWithValue("$createdAt", createdAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$expiresAt", expiresAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$protectedSecret", protectedSecret);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteAsync(IServiceProvider services, string sql)
    {
        var database = services.GetRequiredService<SqliteDatabase>();
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string?> ReadDeviceContactAsync(IServiceProvider services)
    {
        var database = services.GetRequiredService<SqliteDatabase>();
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT last_server_contact_at FROM device_registry WHERE id = $deviceId;";
        command.Parameters.AddWithValue("$deviceId", DeviceId);
        return await command.ExecuteScalarAsync() as string;
    }

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static async Task RunWithServerAsync(
        Func<WebApplication, HttpClient, string, Task> test)
    {
        var directoryPath = Path.Combine(
            Path.GetTempPath(), "MeimadPlanner.EInk.Tests", Guid.NewGuid().ToString("N"));
        var packageRoot = Path.Combine(directoryPath, "packages");
        var application = ServerApplication.Build(
            [
                "--Server:Host=127.0.0.1",
                "--Server:Port=5099",
                $"--Database:Path={Path.Combine(directoryPath, "api-test.db")}",
                $"--EInk:PackageRoot={packageRoot}"
            ],
            webHost => webHost.UseTestServer());
        var started = false;
        try
        {
            await application.StartAsync();
            started = true;
            using var client = application.GetTestClient();
            await test(application, client, packageRoot);
        }
        finally
        {
            if (started) await application.StopAsync();
            await application.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }
}
