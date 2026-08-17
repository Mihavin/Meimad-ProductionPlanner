using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;

namespace Meimad.Planner.Server.Application.Kitaron;

internal sealed partial class KitaronConnectionService
{
    private readonly IKitaronConnectionRepository repository;
    private readonly IKitaronMappingRepository mappingRepository;
    private readonly IKitaronConnectionTester tester;
    private readonly IDataProtector passwordProtector;
    private readonly TimeProvider timeProvider;

    public KitaronConnectionService(
        IKitaronConnectionRepository repository,
        IKitaronMappingRepository mappingRepository,
        IKitaronConnectionTester tester,
        IDataProtectionProvider dataProtectionProvider,
        TimeProvider timeProvider)
    {
        this.repository = repository;
        this.mappingRepository = mappingRepository;
        this.tester = tester;
        passwordProtector = dataProtectionProvider.CreateProtector(
            "Meimad.Planner.Kitaron.SqlPassword.v1");
        this.timeProvider = timeProvider;
    }

    internal async Task<KitaronConnectionSettings> GetAsync(CancellationToken cancellationToken) =>
        Public(await repository.GetAsync(cancellationToken));

    internal async Task<KitaronConnectionSettings> UpdateAsync(
        KitaronConnectionUpdate update,
        CancellationToken cancellationToken)
    {
        var current = await repository.GetAsync(cancellationToken);
        var host = Required(update.ServerHost, "serverHost", 255);
        var databaseName = Identifier(update.DatabaseName, "databaseName");
        var viewSchema = Identifier(update.ViewSchema, "viewSchema");
        var viewName = Identifier(update.ViewName, "viewName");
        var username = Required(update.Username, "username", 128);
        if (update.ServerPort is < 1 or > 65535)
        {
            throw new KitaronConnectionValidationException(
                "serverPort", "serverPort must be between 1 and 65535.");
        }
        if (update.RefreshIntervalSeconds is < 30 or > 86400)
        {
            throw new KitaronConnectionValidationException(
                "refreshIntervalSeconds",
                "refreshIntervalSeconds must be between 30 and 86400.");
        }
        if (update.ClearPassword && !string.IsNullOrEmpty(update.Password))
        {
            throw new KitaronConnectionValidationException(
                "password", "password cannot be supplied when clearPassword is true.");
        }

        var protectedPassword = current.ProtectedPassword;
        if (update.ClearPassword)
        {
            protectedPassword = null;
        }
        else if (!string.IsNullOrEmpty(update.Password))
        {
            if (update.Password.Length > 512)
            {
                throw new KitaronConnectionValidationException(
                    "password", "password must contain at most 512 characters.");
            }
            protectedPassword = passwordProtector.Protect(update.Password);
        }
        if (update.Enabled && protectedPassword is null)
        {
            throw new KitaronConnectionValidationException(
                "enabled", "A password must be configured before the connector is enabled.");
        }

        var stored = current with
        {
            ServerHost = host,
            ServerPort = update.ServerPort,
            DatabaseName = databaseName,
            ViewSchema = viewSchema,
            ViewName = viewName,
            Username = username,
            ProtectedPassword = protectedPassword,
            Enabled = update.Enabled,
            RefreshIntervalSeconds = update.RefreshIntervalSeconds,
            UpdatedAt = timeProvider.GetUtcNow()
        };
        return Public(await repository.UpdateAsync(
            stored,
            update.ExpectedVersion,
            cancellationToken));
    }

    internal async Task<KitaronConnectionTestResult> TestAsync(CancellationToken cancellationToken)
    {
        var settings = await repository.GetAsync(cancellationToken);
        if (settings.ProtectedPassword is null)
        {
            return await RecordFailureAsync(
                "No Kitaron password is configured.", cancellationToken);
        }

        string password;
        try
        {
            password = passwordProtector.Unprotect(settings.ProtectedPassword);
        }
        catch (Exception exception) when (
            exception is System.Security.Cryptography.CryptographicException
            or FormatException)
        {
            return await RecordFailureAsync(
                "The stored Kitaron password cannot be decrypted on this Server machine. Save it again.",
                cancellationToken);
        }

        try
        {
            var columns = await tester.TestAsync(settings, password, cancellationToken);
            await mappingRepository.RecordDetectedColumnsAsync(
                columns, timeProvider.GetUtcNow(), cancellationToken);
            var message = $"Read-only connection succeeded. {columns.Count} source columns were found.";
            var updated = await repository.RecordTestAsync(
                true,
                timeProvider.GetUtcNow(),
                message,
                columns.Count,
                cancellationToken);
            return new KitaronConnectionTestResult(true, message, columns, Public(updated));
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException)
        {
            var message = SafeMessage(exception.Message);
            return await RecordFailureAsync(message, cancellationToken);
        }
    }

    private async Task<KitaronConnectionTestResult> RecordFailureAsync(
        string message,
        CancellationToken cancellationToken)
    {
        var safeMessage = SafeMessage(message);
        var updated = await repository.RecordTestAsync(
            false,
            timeProvider.GetUtcNow(),
            safeMessage,
            null,
            cancellationToken);
        return new KitaronConnectionTestResult(false, safeMessage, [], Public(updated));
    }

    private static KitaronConnectionSettings Public(StoredKitaronConnectionSettings value) => new(
        value.ServerHost,
        value.ServerPort,
        value.DatabaseName,
        value.ViewSchema,
        value.ViewName,
        value.Username,
        value.ProtectedPassword is not null,
        value.Enabled,
        value.RefreshIntervalSeconds,
        value.LastTestStatus,
        value.LastTestAt,
        value.LastTestMessage,
        value.LastTestColumnCount,
        value.Version,
        value.UpdatedAt);

    private static string Required(string? value, string field, int maximum)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized))
        {
            throw new KitaronConnectionValidationException(field, $"{field} is required.");
        }
        if (normalized.Length > maximum)
        {
            throw new KitaronConnectionValidationException(
                field, $"{field} must contain at most {maximum} characters.");
        }
        return normalized;
    }

    private static string Identifier(string? value, string field)
    {
        var normalized = Required(value, field, 128);
        if (!SqlIdentifier().IsMatch(normalized))
        {
            throw new KitaronConnectionValidationException(
                field,
                $"{field} may contain letters, digits, underscore, $, #, or @ and cannot start with a digit.");
        }
        return normalized;
    }

    private static string SafeMessage(string value)
    {
        var normalized = string.IsNullOrWhiteSpace(value)
            ? "The read-only Kitaron connection failed."
            : value.Trim();
        return normalized.Length <= 1000 ? normalized : normalized[..1000];
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_$#@]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SqlIdentifier();
}

internal sealed class KitaronConnectionValidationException(
    string field,
    string message) : Exception(message)
{
    internal string Field { get; } = field;
}
