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
    private const string Token = "mp_eink_test-token-1";

    [Fact]
    public async Task Device_reads_version_screen_manifest_file_and_time_configuration()
    {
        await RunWithServerAsync(async (application, client, packageRoot) =>
        {
            var fileBytes = await SeedAsync(application.Services, packageRoot);
            using var versionRequest = Get($"/api/v1/eink/devices/{DeviceId}/version");
            using var versionResponse = await client.SendAsync(versionRequest);
            Assert.Equal(HttpStatusCode.OK, versionResponse.StatusCode);
            Assert.NotNull(versionResponse.Headers.ETag);
            using var version = JsonDocument.Parse(await versionResponse.Content.ReadAsStringAsync());
            Assert.Equal("machine-eink-1", version.RootElement.GetProperty("machineId").GetString());
            Assert.Equal("package-eink-1", version.RootElement.GetProperty("package").GetProperty("packageId").GetString());

            using var conditional = Get($"/api/v1/eink/devices/{DeviceId}/version");
            conditional.Headers.IfNoneMatch.Add(versionResponse.Headers.ETag!);
            using var unchanged = await client.SendAsync(conditional);
            Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);

            using var screenResponse = await client.SendAsync(Get(
                $"/api/v1/eink/devices/{DeviceId}/machine-screen"));
            using var screen = JsonDocument.Parse(await screenResponse.Content.ReadAsStringAsync());
            Assert.Equal("M-EINK-1", screen.RootElement.GetProperty("machine").GetProperty("number").GetString());
            Assert.Equal("operation-eink-1", screen.RootElement.GetProperty("current").GetProperty("batchOperationId").GetString());
            Assert.Equal(3, screen.RootElement.GetProperty("next").GetArrayLength());
            Assert.Equal("current", screen.RootElement.GetProperty("status").GetProperty("code").GetString());

            using var manifestResponse = await client.SendAsync(Get(
                $"/api/v1/eink/devices/{DeviceId}/package-manifest"));
            Assert.Equal(
                $"/api/v1/eink/devices/{DeviceId}/packages/package-eink-1/revisions/R1/manifest",
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
                $"/api/v1/eink/devices/{DeviceId}/time-config"));
            using var time = JsonDocument.Parse(await timeResponse.Content.ReadAsStringAsync());
            Assert.Equal("Asia/Jerusalem", time.RootElement.GetProperty("timeZoneId").GetString());
            Assert.Equal(300, time.RootElement.GetProperty("pollIntervalSeconds").GetInt32());
        });
    }

    [Fact]
    public async Task Device_credentials_are_scoped_revocable_and_read_only()
    {
        await RunWithServerAsync(async (application, client, packageRoot) =>
        {
            await SeedAsync(application.Services, packageRoot);

            using var wrongToken = Get($"/api/v1/eink/devices/{DeviceId}/version", "mp_eink_wrong");
            using var wrongResponse = await client.SendAsync(wrongToken);
            Assert.Equal(HttpStatusCode.NotFound, wrongResponse.StatusCode);

            using var otherDevice = Get("/api/v1/eink/devices/device-eink-2/version", Token);
            using var otherResponse = await client.SendAsync(otherDevice);
            Assert.Equal(HttpStatusCode.NotFound, otherResponse.StatusCode);

            using var planningRead = Get("/api/v1/cases");
            using var planningReadResponse = await client.SendAsync(planningRead);
            Assert.Equal(HttpStatusCode.Forbidden, planningReadResponse.StatusCode);

            using var spacedCredential = new HttpRequestMessage(HttpMethod.Get, "/api/v1/cases");
            spacedCredential.Headers.TryAddWithoutValidation("Authorization", $"Bearer    {Token}");
            using var spacedCredentialResponse = await client.SendAsync(spacedCredential);
            Assert.Equal(HttpStatusCode.Forbidden, spacedCredentialResponse.StatusCode);

            using var mutation = new HttpRequestMessage(HttpMethod.Post, "/api/v1/edit-mode/requests");
            mutation.Headers.Authorization = new("bearer", Token);
            mutation.Headers.Add("X-Meimad-Client-Id", "device-client");
            mutation.Headers.Add("X-Meimad-User-Id", "device-user");
            using var mutationResponse = await client.SendAsync(mutation);
            Assert.Equal(HttpStatusCode.Forbidden, mutationResponse.StatusCode);

            await SetDeviceEnabledAsync(application.Services, false);
            using var revokedResponse = await client.SendAsync(Get(
                $"/api/v1/eink/devices/{DeviceId}/machine-screen"));
            Assert.Equal(HttpStatusCode.NotFound, revokedResponse.StatusCode);
        });
    }

    [Fact]
    public async Task Tablet_bootstrap_requires_matching_enabled_token_and_hardware_id()
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
            Assert.Equal(DeviceId, document.RootElement.GetProperty("deviceId").GetString());
            Assert.Equal("machine-eink-1", document.RootElement.GetProperty("machineId").GetString());

            using var wrongHardware = await client.SendAsync(Get(
                "/api/tablet/ping?hardwareId=A4:CF:12:83:76:92"));
            Assert.Equal(HttpStatusCode.NotFound, wrongHardware.StatusCode);

            using var otherToken = await client.SendAsync(Get(
                "/api/tablet/ping?hardwareId=A4:CF:12:83:76:91", "mp_eink_other-token"));
            Assert.Equal(HttpStatusCode.NotFound, otherToken.StatusCode);

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
            Assert.DoesNotContain(Token, json, StringComparison.Ordinal);
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
            using var wrongToken = await client.SendAsync(Get(
                "/api/tablets/3041/status", "mp_eink_other-token"));
            Assert.Equal(HttpStatusCode.NotFound, wrongToken.StatusCode);
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

            using var firstResponse = await client.SendAsync(PostEvent("3041", Token,
                new { event_type = "SEND_TO_QC" }));
            Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
            using var first = JsonDocument.Parse(await firstResponse.Content.ReadAsStringAsync());
            Assert.Equal("3041", first.RootElement.GetProperty("tablet_id").GetString());
            Assert.Equal("SEND_TO_QC", first.RootElement.GetProperty("event_type").GetString());
            Assert.False(first.RootElement.GetProperty("duplicate").GetBoolean());
            var acceptedAt = first.RootElement.GetProperty("timestamp").GetDateTimeOffset();

            using var retryResponse = await client.SendAsync(PostEvent("3041", Token,
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

            using var tooEarly = await client.SendAsync(PostEvent("3041", Token,
                new { event_type = "SEND_TO_QC" }));
            Assert.Equal(HttpStatusCode.Conflict, tooEarly.StatusCode);

            await EnterSetupRunAsync(application.Services);
            using var wrongTablet = await client.SendAsync(PostEvent("3042", Token,
                new { event_type = "SEND_TO_QC" }));
            Assert.Equal(HttpStatusCode.NotFound, wrongTablet.StatusCode);
            using var wrongToken = await client.SendAsync(PostEvent(
                "3041", "mp_eink_other-token", new { event_type = "SEND_TO_QC" }));
            Assert.Equal(HttpStatusCode.NotFound, wrongToken.StatusCode);
            using var wrongEvent = await client.SendAsync(PostEvent("3041", Token,
                new { event_type = "QC_PASS" }));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, wrongEvent.StatusCode);
            using var suppliedTarget = await client.SendAsync(PostEvent("3041", Token,
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
                using var response = await client.SendAsync(PostEvent("3041", Token,
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

            using var wrongToken = await client.SendAsync(Get(
                "/api/tablets/3041/status", "mp_eink_other-token"));
            Assert.Equal(HttpStatusCode.NotFound, wrongToken.StatusCode);

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
                $"/api/v1/eink/devices/{DeviceId}/packages/package-eink-1/revisions/R1/files/file-eink-1"));
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("package_integrity_failed", document.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.DoesNotContain("corrupted package content", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Active_editor_can_register_bind_revoke_and_rotate_a_device_token()
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
            var firstToken = created.RootElement.GetProperty("registrationToken").GetString()!;
            Assert.StartsWith("mp_eink_", firstToken, StringComparison.Ordinal);

            await UnbindDeviceAsync(application.Services, DeviceId);
            using var updateRequest = new HttpRequestMessage(
                HttpMethod.Patch,
                $"/api/v1/eink/device-registrations/{deviceId}")
            {
                Content = JsonContent.Create(new
                {
                    machineId = "machine-eink-1",
                    isEnabled = true,
                    rotateCredential = true
                })
            };
            using var update = await client.SendAsync(updateRequest);
            Assert.Equal(HttpStatusCode.OK, update.StatusCode);
            using var updated = JsonDocument.Parse(await update.Content.ReadAsStringAsync());
            var rotatedToken = updated.RootElement.GetProperty("registrationToken").GetString()!;
            Assert.NotEqual(firstToken, rotatedToken);

            using var oldCredential = await client.SendAsync(Get(
                $"/api/v1/eink/devices/{deviceId}/version",
                firstToken));
            Assert.Equal(HttpStatusCode.NotFound, oldCredential.StatusCode);
            using var newCredential = await client.SendAsync(Get(
                $"/api/v1/eink/devices/{deviceId}/version",
                rotatedToken));
            Assert.Equal(HttpStatusCode.OK, newCredential.StatusCode);

            using var revokeRequest = new HttpRequestMessage(
                HttpMethod.Patch,
                $"/api/v1/eink/device-registrations/{deviceId}")
            {
                Content = JsonContent.Create(new
                {
                    machineId = "machine-eink-1",
                    isEnabled = false,
                    rotateCredential = false
                })
            };
            using var revoke = await client.SendAsync(revokeRequest);
            Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
            using var revoked = await client.SendAsync(Get(
                $"/api/v1/eink/devices/{deviceId}/version",
                rotatedToken));
            Assert.Equal(HttpStatusCode.NotFound, revoked.StatusCode);
        });
    }

    [Fact]
    public async Task Simulator_is_local_read_only_and_has_no_write_back_or_usb_surface()
    {
        await RunWithServerAsync(async (_, client, _) =>
        {
            using var page = await client.GetAsync("/eink-simulator/");
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            var html = await page.Content.ReadAsStringAsync();
            Assert.Contains("READ-ONLY", html, StringComparison.Ordinal);
            Assert.Contains("NO WRITE-BACK", html, StringComparison.Ordinal);
            Assert.DoesNotContain("textarea", html, StringComparison.OrdinalIgnoreCase);

            using var script = await client.GetAsync("/eink-simulator/app.js");
            var javascript = await script.Content.ReadAsStringAsync();
            Assert.Contains("GET version (small change check)", javascript, StringComparison.Ordinal);
            Assert.Contains("crypto.subtle.digest", javascript, StringComparison.Ordinal);
            Assert.DoesNotContain("method: \"POST\"", javascript, StringComparison.Ordinal);
            Assert.DoesNotContain("edit-mode", javascript, StringComparison.OrdinalIgnoreCase);

            using var usb = await client.SendAsync(Get(
                $"/api/v1/eink/devices/{DeviceId}/usb-mass-storage"));
            Assert.Equal(HttpStatusCode.NotFound, usb.StatusCode);
        });
    }

    private static HttpRequestMessage Get(string path, string token = Token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new("Bearer", token);
        return request;
    }

    private static HttpRequestMessage PostEvent(
        string tabletId,
        string token,
        object body)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/tablets/{tabletId}/events")
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Authorization = new("Bearer", token);
        request.Headers.Add("X-Meimad-Battery-Voltage", "3.850");
        return request;
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
                id, tablet_id, hardware_id, device_type, device_name, machine_id, credential_hash,
                access_mode, is_enabled)
            VALUES
                ('device-eink-1', '3041', 'A4:CF:12:83:76:91', 'eink', 'Tablet One', 'machine-eink-1',
                 $credentialHash, 'read_only', 1),
                ('device-eink-2', '3042', 'A4:CF:12:83:76:92', 'eink', 'Tablet Two', NULL,
                 $otherCredentialHash, 'read_only', 1);
            INSERT INTO eink_package_revisions (
                id, batch_operation_id, revision, tool_cart_id, published_at)
            VALUES ('package-eink-1', 'operation-eink-1', 'R1', 'TC-12', $publishedAt);
            INSERT INTO eink_package_files (
                id, package_revision_id, logical_path, storage_relative_path,
                media_type, byte_length, sha256, modified_at, display_order)
            VALUES (
                'file-eink-1', 'package-eink-1', 'instructions/setup.txt',
                'package-eink-1/setup.txt', 'text/plain; charset=utf-8',
                $byteLength, $sha256, $publishedAt, 0);
            """;
        command.Parameters.AddWithValue("$calendar", calendar);
        command.Parameters.AddWithValue("$credentialHash", Sha256(Encoding.UTF8.GetBytes(Token)));
        command.Parameters.AddWithValue("$otherCredentialHash", Sha256(Encoding.UTF8.GetBytes("mp_eink_other-token")));
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
