using System.Security.Cryptography;
using System.Text.Json;
using Meimad.Planner.Server.Application.EditMode;
using Microsoft.AspNetCore.DataProtection;

namespace Meimad.Planner.Server.Application.Cnc;

internal sealed record OffsetLoaderRelease(
    string OffsetLoaderReleaseId, string ProductionRunId, string MachineId,
    string NcReleaseId, string ToolTableReleaseId, int VerificationReleaseToken,
    string? ArtifactHash, DateTimeOffset CreatedAt, string CreatedBy,
    string MetadataJson, bool IsCurrent);

internal sealed record CreateOffsetLoaderRelease(
    string MachineId, string NcReleaseId, string ToolTableReleaseId,
    string? ArtifactHash = null, string MetadataJson = "{}");

internal sealed record StoredCncVerificationSettings(
    string MachineId, string DprintTransport, int DprintPort,
    int ChallengeProgramNumber, int VerifyProgramNumber, int? CustomGcodeAlias,
    int NonceVariable, int ResponseVariable, int VerificationStateVariable,
    int ReleaseTokenVariable, string ProtectedSecret, int ExpectedMacroVersion,
    int ResponseCodeDigits, int VerificationTimeoutSeconds, bool Enabled,
    int Version, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

internal sealed record CncVerificationSettings(
    string MachineId, string DprintTransport, int DprintPort,
    int ChallengeProgramNumber, int VerifyProgramNumber, int? CustomGcodeAlias,
    int NonceVariable, int ResponseVariable, int VerificationStateVariable,
    int ReleaseTokenVariable, bool SecretConfigured, int ExpectedMacroVersion,
    int ResponseCodeDigits, int VerificationTimeoutSeconds, bool Enabled,
    int Version, DateTimeOffset UpdatedAt);

internal sealed record UpdateCncVerificationSettings(
    string DprintTransport, int DprintPort, int ChallengeProgramNumber,
    int VerifyProgramNumber, int? CustomGcodeAlias, int NonceVariable,
    int ResponseVariable, int VerificationStateVariable, int ReleaseTokenVariable,
    string? VerificationSecret, int ExpectedMacroVersion, int ResponseCodeDigits,
    int VerificationTimeoutSeconds, bool Enabled);

internal sealed record CncDprintIngestionContext(
    string ProductionRunId, string MachineId, string OffsetLoaderReleaseId,
    string NcReleaseId, int VerificationReleaseToken, int ExpectedMacroVersion);

internal interface ICncVerificationFoundationRepository
{
    Task<OffsetLoaderRelease> CreateOffsetLoaderReleaseAsync(
        string productionRunId, CreateOffsetLoaderRelease command, int releaseToken,
        DateTimeOffset createdAt, EditAuthority authority, CancellationToken token);
    Task<IReadOnlyList<OffsetLoaderRelease>> ListOffsetLoaderReleasesAsync(
        string productionRunId, CancellationToken token);
    Task<StoredCncVerificationSettings?> GetSettingsAsync(string machineId, CancellationToken token);
    Task<StoredCncVerificationSettings> UpsertSettingsAsync(
        StoredCncVerificationSettings settings, int expectedVersion,
        EditAuthority authority, CancellationToken token);
    Task<CncDprintIngestionContext?> ResolveCurrentOffsetLoaderAsync(
        string machineId, int verificationReleaseToken, CancellationToken token);
}

internal sealed class CncVerificationFoundationService
{
    private readonly ICncVerificationFoundationRepository repository;
    private readonly TimeProvider timeProvider;
    private readonly IDataProtector secretProtector;

    internal CncVerificationFoundationService(
        ICncVerificationFoundationRepository repository,
        TimeProvider timeProvider,
        IDataProtectionProvider dataProtectionProvider)
    {
        this.repository = repository;
        this.timeProvider = timeProvider;
        secretProtector = dataProtectionProvider.CreateProtector(
            "Meimad.Planner.CncVerification.MachineSecret.v1");
    }

    internal async Task<OffsetLoaderRelease> CreateOffsetLoaderReleaseAsync(
        string productionRunId, CreateOffsetLoaderRelease command,
        EditAuthority authority, CancellationToken token = default)
    {
        Required(productionRunId, "productionRunId");
        Required(command.MachineId, "machineId");
        Required(command.NcReleaseId, "ncReleaseId");
        Required(command.ToolTableReleaseId, "toolTableReleaseId");
        if (command.ArtifactHash is not null
            && (command.ArtifactHash.Length != 64 || !command.ArtifactHash.All(Uri.IsHexDigit)))
            throw new CncVerificationValidationException("artifactHash", "invalid_hash",
                "artifactHash must be a 64-character hexadecimal SHA-256 value.");
        JsonObject(command.MetadataJson, "metadataJson");
        var releaseToken = RandomNumberGenerator.GetInt32(100000, 1000000);
        return await repository.CreateOffsetLoaderReleaseAsync(
            productionRunId.Trim(), command, releaseToken, timeProvider.GetUtcNow(), authority, token);
    }

    internal Task<IReadOnlyList<OffsetLoaderRelease>> ListOffsetLoaderReleasesAsync(
        string productionRunId, CancellationToken token = default)
    {
        Required(productionRunId, "productionRunId");
        return repository.ListOffsetLoaderReleasesAsync(productionRunId.Trim(), token);
    }

    internal async Task<CncVerificationSettings?> GetSettingsAsync(
        string machineId, CancellationToken token = default)
    {
        Required(machineId, "machineId");
        var value = await repository.GetSettingsAsync(machineId.Trim(), token);
        return value is null ? null : Public(value);
    }

    internal async Task<CncVerificationSettings> UpdateSettingsAsync(
        string machineId, UpdateCncVerificationSettings command, int expectedVersion,
        EditAuthority authority, CancellationToken token = default)
    {
        Required(machineId, "machineId");
        if (command.DprintTransport != "HAAS_DPRNT_TCP")
            throw new CncVerificationValidationException("dprintTransport", "unsupported_transport",
                "dprintTransport must be HAAS_DPRNT_TCP.");
        Range(command.DprintPort, 1, 65535, "dprintPort");
        Range(command.ChallengeProgramNumber, 9000, 9999, "challengeProgramNumber");
        Range(command.VerifyProgramNumber, 9000, 9999, "verifyProgramNumber");
        if (command.ChallengeProgramNumber == command.VerifyProgramNumber)
            throw new CncVerificationValidationException("verifyProgramNumber", "program_collision",
                "Challenge and verification protected programs must be different.");
        if (command.CustomGcodeAlias.HasValue) Range(command.CustomGcodeAlias.Value, 1, 999, "customGcodeAlias");
        var variables = new[] { command.NonceVariable, command.ResponseVariable,
            command.VerificationStateVariable, command.ReleaseTokenVariable };
        foreach (var value in variables) Range(value, 1, 10999, "verificationVariables");
        if (variables.Distinct().Count() != variables.Length)
            throw new CncVerificationValidationException("verificationVariables", "variable_collision",
                "Verification variables must be distinct.");
        if (command.ExpectedMacroVersion <= 0)
            throw new CncVerificationValidationException("expectedMacroVersion", "out_of_range",
                "expectedMacroVersion must be positive.");
        Range(command.ResponseCodeDigits, 4, 6, "responseCodeDigits");
        Range(command.VerificationTimeoutSeconds, 30, 3600, "verificationTimeoutSeconds");

        var existing = await repository.GetSettingsAsync(machineId.Trim(), token);
        string protectedSecret;
        if (!string.IsNullOrWhiteSpace(command.VerificationSecret))
        {
            var secret = command.VerificationSecret.Trim();
            if (secret.Length is < 16 or > 256)
                throw new CncVerificationValidationException("verificationSecret", "invalid_length",
                    "verificationSecret must contain 16 to 256 characters.");
            protectedSecret = secretProtector.Protect(secret);
        }
        else if (existing is not null) protectedSecret = existing.ProtectedSecret;
        else throw new CncVerificationValidationException("verificationSecret", "required",
            "verificationSecret is required when creating Machine verification settings.");

        var now = timeProvider.GetUtcNow();
        var stored = new StoredCncVerificationSettings(
            machineId.Trim(), command.DprintTransport, command.DprintPort,
            command.ChallengeProgramNumber, command.VerifyProgramNumber,
            command.CustomGcodeAlias, command.NonceVariable, command.ResponseVariable,
            command.VerificationStateVariable, command.ReleaseTokenVariable,
            protectedSecret, command.ExpectedMacroVersion, command.ResponseCodeDigits,
            command.VerificationTimeoutSeconds, command.Enabled, expectedVersion + 1,
            existing?.CreatedAt ?? now, now);
        return Public(await repository.UpsertSettingsAsync(stored, expectedVersion, authority, token));
    }

    private static CncVerificationSettings Public(StoredCncVerificationSettings value) => new(
        value.MachineId, value.DprintTransport, value.DprintPort,
        value.ChallengeProgramNumber, value.VerifyProgramNumber, value.CustomGcodeAlias,
        value.NonceVariable, value.ResponseVariable, value.VerificationStateVariable,
        value.ReleaseTokenVariable, true, value.ExpectedMacroVersion,
        value.ResponseCodeDigits, value.VerificationTimeoutSeconds, value.Enabled,
        value.Version, value.UpdatedAt);

    private static void JsonObject(string json, string field)
    {
        try
        {
            using var value = JsonDocument.Parse(json);
            if (value.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
        }
        catch (JsonException)
        {
            throw new CncVerificationValidationException(field, "invalid_json",
                $"{field} must be a JSON object.");
        }
    }

    private static void Required(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new CncVerificationValidationException(field, "required", $"{field} is required.");
    }
    private static void Range(int value, int minimum, int maximum, string field)
    {
        if (value < minimum || value > maximum)
            throw new CncVerificationValidationException(field, "out_of_range",
                $"{field} must be between {minimum} and {maximum}.");
    }
}

internal sealed class CncVerificationValidationException(string field, string code, string message)
    : Exception(message)
{
    internal string Field { get; } = field;
    internal string Code { get; } = code;
}
internal sealed class CncVerificationTargetException(string code, string message) : Exception(message)
{
    internal string Code { get; } = code;
}
internal sealed class CncVerificationConcurrencyException() : Exception("Verification settings were changed by another editor.");
