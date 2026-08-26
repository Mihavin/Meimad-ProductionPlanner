using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Meimad.Planner.Server.Application.Cnc;
using Microsoft.AspNetCore.DataProtection;

namespace Meimad.Planner.Server.Application.EInk;

internal sealed class TabletStatusService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ITabletStatusRepository repository;
    private readonly IEInkDeviceRegistrationRepository registrations;
    private readonly IDataProtector verificationSecretProtector;
    private readonly ILogger<TabletStatusService> logger;

    public TabletStatusService(
        ITabletStatusRepository repository,
        IEInkDeviceRegistrationRepository registrations,
        IDataProtectionProvider dataProtectionProvider,
        ILogger<TabletStatusService> logger)
    {
        this.repository = repository;
        this.registrations = registrations;
        verificationSecretProtector = dataProtectionProvider.CreateProtector(
            CncVerificationSecretProtection.Purpose);
        this.logger = logger;
    }

    internal async Task<TabletStatusResponse> ReadAsync(
        string tabletId,
        string bearerToken,
        DateTimeOffset contactedAt,
        decimal? batteryVoltage,
        int? batteryPercent,
        CancellationToken cancellationToken = default)
    {
        var source = await repository.ReadAsync(tabletId.Trim(), cancellationToken);
        if (source is null
            || !source.IsEnabled
            || string.IsNullOrWhiteSpace(source.CredentialHash)
            || !FixedEquals(HashToken(bearerToken), source.CredentialHash))
        {
            throw new TabletStatusResourceNotFoundException();
        }

        await registrations.RecordContactAsync(
            source.DeviceId,
            contactedAt,
            batteryVoltage,
            batteryPercent,
            cancellationToken);

        if (source.Machine is null)
        {
            throw new TabletStatusResourceNotFoundException();
        }

        if (source.Run is null)
        {
            throw new TabletStatusUnavailableException(
                "tablet_no_current_run",
                "No current Production Run is assigned to the tablet's Machine.");
        }

        if (source.Outputs.Count != 1)
        {
            throw new TabletStatusUnavailableException(
                "tablet_projection_ambiguous",
                "The current Production Run cannot be represented by the single-output tablet status contract.");
        }

        var output = source.Outputs[0];
        var status = Status(source.Machine, source.Run, source.Workflow);
        var verification = Verification(source, status, contactedAt);
        var content = new
        {
            tabletId = source.TabletId,
            machine = source.Machine,
            run = source.Run.RunId,
            output,
            status,
            workflowEvent = source.Workflow?.EventId,
            verification
        };
        return new TabletStatusResponse(
            Revision(content),
            source.TabletId,
            new TabletStatusMachineResponse(
                source.Machine.MachineId,
                source.Machine.Number,
                source.Machine.Name),
            new TabletStatusRunResponse(source.Run.RunId),
            new TabletStatusPartResponse(output.PartNumber, output.PartName),
            new TabletStatusOperationResponse(output.OperationNumber, output.OperationName),
            status,
            verification);
    }

    private TabletStatusVerificationResponse? Verification(
        TabletStatusSource source, string status, DateTimeOffset now)
    {
        if (status != "IN_SETUP") return null;
        var session = source.VerificationSession;
        if (session is null)
            return new(true, "UNAVAILABLE", null);
        if (session.State != "PENDING" || !session.ContextIsValid)
            return new(true, "INVALIDATED", null);
        if (now >= session.ExpiresAt)
            return new(true, "EXPIRED", null);

        try
        {
            var secret = verificationSecretProtector.Unprotect(session.ProtectedSecret);
            var machineKey = CncVerificationResponseAlgorithm.DeriveMachineKey(
                source.Machine!.MachineId, secret);
            var response = CncVerificationResponseAlgorithm.Calculate(
                session.Nonce, session.OffsetLoaderReleaseToken,
                session.NcIdentityToken, machineKey, session.ResponseCodeDigits);
            return new(true, "WAITING_FOR_OPERATOR", response);
        }
        catch (CryptographicException exception)
        {
            logger.LogError(exception,
                "Unable to decrypt CNC verification configuration. MachineId={MachineId} SessionId={SessionId}",
                source.Machine!.MachineId, session.SessionId);
            return new(true, "UNAVAILABLE", null);
        }
    }

    private static string Status(
        TabletStatusMachineSource machine,
        TabletStatusRunSource run,
        TabletStatusWorkflowSource? workflow)
    {
        if (!machine.IsActive || run.Status == "SUSPENDED")
        {
            return "BLOCKED";
        }

        return workflow?.EventType switch
        {
            null => "READY_FOR_SETUP",
            "OFFSET_LOADER_COMPLETED" or "SETUP_VERIFICATION_REQUESTED"
                or "SETUP_VERIFICATION_FAILED" => "IN_SETUP",
            "SETUP_VERIFICATION_SUCCEEDED" or "QC_FAIL" => "IN_SETUP_RUN",
            "SEND_TO_QC" => "IN_QC",
            "QC_PASS" => "READY_FOR_PRODUCTION",
            "CYCLE_START" or "CYCLE_END" or "CYCLE_INTERRUPTED"
                or "PRODUCTION_SESSION_OPENED" => "IN_PRODUCTION",
            "PRODUCTION_SESSION_CLOSED" => "UNKNOWN",
            _ => "UNKNOWN"
        };
    }

    private static uint Revision<T>(T content)
    {
        var hash = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(content, JsonOptions));
        return BinaryPrimitives.ReadUInt32BigEndian(hash);
    }

    private static string HashToken(string token) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(token ?? string.Empty))).ToLowerInvariant();

    private static bool FixedEquals(string left, string right)
    {
        var leftBytes = Encoding.ASCII.GetBytes(left);
        var rightBytes = Encoding.ASCII.GetBytes(right);
        return leftBytes.Length == rightBytes.Length
            && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}

internal sealed record TabletStatusResponse(
    uint Revision,
    [property: JsonPropertyName("tablet_id")]
    string TabletId,
    TabletStatusMachineResponse Machine,
    [property: JsonPropertyName("nc_run")]
    TabletStatusRunResponse NcRun,
    TabletStatusPartResponse Part,
    TabletStatusOperationResponse Operation,
    string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    TabletStatusVerificationResponse? Verification);

internal sealed record TabletStatusMachineResponse(string Id, string Number, string Name);

internal sealed record TabletStatusRunResponse(string Id);

internal sealed record TabletStatusPartResponse(string Number, string Name);

internal sealed record TabletStatusOperationResponse(int Number, string Name);

internal sealed record TabletStatusVerificationResponse(
    bool Required,
    string State,
    [property: JsonPropertyName("response_code")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ResponseCode);

internal sealed class TabletStatusResourceNotFoundException : Exception;

internal sealed class TabletStatusUnavailableException : Exception
{
    internal TabletStatusUnavailableException(string code, string message) : base(message)
    {
        Code = code;
    }

    internal string Code { get; }
}
