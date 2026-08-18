using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Meimad.Planner.Server.Configuration;
using Microsoft.AspNetCore.DataProtection;

namespace Meimad.Planner.Server.Application.Kitaron;

internal sealed class KitaronSyncService
{
    private readonly IKitaronConnectionRepository connectionRepository;
    private readonly KitaronMappingService mappingService;
    private readonly IKitaronSourceReader sourceReader;
    private readonly IKitaronSyncRepository syncRepository;
    private readonly IDataProtector passwordProtector;
    private readonly TimeProvider timeProvider;
    private readonly string workingFolderRoot;
    private readonly ILogger<KitaronSyncService> logger;
    private readonly SemaphoreSlim gate = new(1, 1);

    public KitaronSyncService(
        IKitaronConnectionRepository connectionRepository,
        KitaronMappingService mappingService,
        IKitaronSourceReader sourceReader,
        IKitaronSyncRepository syncRepository,
        IDataProtectionProvider dataProtectionProvider,
        DatabaseOptions databaseOptions,
        TimeProvider timeProvider,
        ILogger<KitaronSyncService> logger)
    {
        this.connectionRepository = connectionRepository;
        this.mappingService = mappingService;
        this.sourceReader = sourceReader;
        this.syncRepository = syncRepository;
        this.timeProvider = timeProvider;
        this.logger = logger;
        passwordProtector = dataProtectionProvider.CreateProtector("Meimad.Planner.Kitaron.SqlPassword.v1");
        var dataDirectory = Path.GetDirectoryName(databaseOptions.DatabasePath)
            ?? throw new InvalidOperationException("The Planner database path has no parent directory.");
        workingFolderRoot = Path.GetFullPath(Path.Combine(dataDirectory, "KitaronCases"));
    }

    internal Task<KitaronSyncStatus> GetStatusAsync(CancellationToken cancellationToken) =>
        syncRepository.GetStatusAsync(cancellationToken);

    internal async Task<KitaronSyncStatus> RunAsync(CancellationToken cancellationToken)
    {
        if (!await gate.WaitAsync(0, cancellationToken))
            throw new KitaronSyncBlockedException("A Kitaron synchronization is already running.");
        try
        {
            var mapping = await mappingService.GetAsync(cancellationToken);
            var now = timeProvider.GetUtcNow();
            await syncRepository.MarkStartedAsync(mapping.Version, now, cancellationToken);
            try
            {
                if (mapping.Status != "ready_for_implementation")
                    throw new KitaronSyncBlockedException("Save the mapping as Ready before synchronization.");
                var connection = await connectionRepository.GetAsync(cancellationToken);
                if (!connection.Enabled)
                    throw new KitaronSyncBlockedException("Enable the Kitaron connector before synchronization.");
                if (connection.LastTestStatus != "succeeded")
                    throw new KitaronSyncBlockedException("Run a successful read-only connection test first.");
                if (string.IsNullOrWhiteSpace(connection.ProtectedPassword))
                    throw new KitaronSyncBlockedException("No Kitaron password is configured.");

                string password;
                try { password = passwordProtector.Unprotect(connection.ProtectedPassword); }
                catch (Exception exception)
                {
                    throw new KitaronSyncBlockedException(
                        "The stored password cannot be decrypted on this Server. Save it again.") { Source = exception.Source };
                }

                var active = mapping.Fields
                    .Where(field => field.Enabled
                        && field.ModelModes.Contains(mapping.ModelMode, StringComparer.Ordinal)
                        && field.TargetEntity is "cases" or "orders" or "case_operations")
                    .ToArray();
                var columns = active
                    .Where(field => field.SourceColumn is not null
                        && !StringComparer.OrdinalIgnoreCase.Equals(field.SourceColumn, "auto")
                        && field.Transform != "generated_working_folder")
                    .Select(field => field.SourceColumn!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var rows = await sourceReader.ReadAsync(connection, password, columns, cancellationToken);
                var plan = BuildPlan(rows, active, mapping.Version);
                return await syncRepository.ApplyAsync(plan, timeProvider.GetUtcNow(), cancellationToken);
            }
            catch (KitaronSyncBlockedException exception)
            {
                return await syncRepository.MarkFailedAsync(
                    "blocked", exception.Message, timeProvider.GetUtcNow(), cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Kitaron one-way synchronization failed.");
                return await syncRepository.MarkFailedAsync(
                    "failed", SafeMessage(exception), timeProvider.GetUtcNow(), cancellationToken);
            }
        }
        finally { gate.Release(); }
    }

    private KitaronSyncPlan BuildPlan(
        IReadOnlyList<KitaronSourceRow> rows,
        IReadOnlyList<KitaronMappingField> fields,
        int mappingVersion)
    {
        var byTarget = fields.ToDictionary(
            field => $"{field.TargetEntity}.{field.TargetField}", StringComparer.Ordinal);
        KitaronMappingField Field(string entity, string field) =>
            byTarget.GetValueOrDefault($"{entity}.{field}")
            ?? throw new KitaronSyncBlockedException($"The ready mapping is missing {entity}.{field}.");

        var warnings = new List<string>();
        var parsed = new List<ParsedRow>(rows.Count);
        foreach (var row in rows)
        {
            var part = Text(row, Field("cases", "part_number"));
            if (part is null) { AddWarning(warnings, "A source row without a Part Number was skipped."); continue; }
            var name = Text(row, Field("cases", "name")) ?? part;
            parsed.Add(new ParsedRow(
                part, name,
                OptionalText(row, byTarget, "cases.revision"),
                OptionalText(row, byTarget, "cases.customer"),
                OptionalText(row, byTarget, "orders.order_reference"),
                OptionalInt(row, byTarget, "orders.quantity"),
                OptionalDate(row, byTarget, "orders.work_finish_date"),
                OptionalInt(row, byTarget, "case_operations.operation_number"),
                OptionalInt(row, byTarget, "case_operations.route_position"),
                OptionalText(row, byTarget, "case_operations.name"),
                OptionalText(row, byTarget, "case_operations.required_machine_type", manualLookupAsNull: true),
                OptionalSeconds(row, byTarget, "case_operations.setup_seconds"),
                OptionalSeconds(row, byTarget, "case_operations.cycle_seconds")));
        }

        Directory.CreateDirectory(workingFolderRoot);
        var cases = parsed.GroupBy(row => row.Part, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var first = group.First();
                var revision = Consistent(group.Select(item => item.Revision), group.Key, "revision", warnings);
                var customer = Consistent(group.Select(item => item.Customer), group.Key, "customer", warnings);
                var folder = Path.Combine(workingFolderRoot, SafeFolder(group.Key));
                return new KitaronSyncCase(group.Key, group.Key, first.Name, revision, customer, folder,
                    Hash(group.Key, first.Name, revision, customer, folder));
            }).OrderBy(item => item.PartNumber, StringComparer.OrdinalIgnoreCase).ToArray();

        var orders = parsed.Where(row => row.OrderNumber is not null)
            .GroupBy(row => $"{row.Part}\u001f{row.OrderNumber}", StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var valid = group.Where(row => row.Quantity > 0 && row.WorkFinishDate is not null).ToArray();
                if (valid.Length == 0)
                {
                    AddWarning(warnings, $"Order {group.First().OrderNumber} was skipped because quantity or finish date is invalid.");
                    return null;
                }
                var first = valid[0];
                var quantity = valid.Max(row => row.Quantity!.Value);
                var date = valid.Min(row => row.WorkFinishDate!.Value);
                return new KitaronSyncOrder(group.Key, first.Part, first.OrderNumber!, quantity, date,
                    Hash(group.Key, quantity, date));
            }).Where(item => item is not null).Cast<KitaronSyncOrder>()
            .OrderBy(item => item.SourceKey, StringComparer.OrdinalIgnoreCase).ToArray();

        var rawOperations = parsed.Where(row => row.OperationNumber > 0)
            .GroupBy(row => $"{row.Part}\u001f{row.OperationNumber}", StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var first = group.First();
                return new RawOperation(group.Key, first.Part, first.OperationNumber!.Value,
                    first.RoutePosition ?? first.OperationNumber.Value, first.OperationName ?? $"Operation {first.OperationNumber}",
                    Consistent(group.Select(item => item.RequiredMachineType), group.Key, "Machine Type", warnings),
                    group.Select(item => item.SetupSeconds).FirstOrDefault(value => value.HasValue),
                    group.Select(item => item.CycleSeconds).FirstOrDefault(value => value.HasValue));
            }).ToArray();
        var operations = rawOperations.GroupBy(item => item.CaseSourceKey, StringComparer.OrdinalIgnoreCase)
            .SelectMany(group => group.OrderBy(item => item.SourcePosition).ThenBy(item => item.OperationNumber)
                .Select((item, index) => new KitaronSyncOperation(
                    item.SourceKey, item.CaseSourceKey, item.OperationNumber, index, item.Name,
                    item.RequiredMachineType, item.SetupSeconds, item.CycleSeconds,
                    Hash(item.SourceKey, index, item.Name, item.RequiredMachineType, item.SetupSeconds, item.CycleSeconds))))
            .OrderBy(item => item.SourceKey, StringComparer.OrdinalIgnoreCase).ToArray();

        return new KitaronSyncPlan(rows.Count, cases, orders, operations, warnings, mappingVersion);
    }

    private static string? OptionalText(KitaronSourceRow row, IReadOnlyDictionary<string, KitaronMappingField> fields,
        string key, bool manualLookupAsNull = false) =>
        fields.TryGetValue(key, out var field) && !manualLookupAsNull ? Text(row, field) : null;

    private static int? OptionalInt(KitaronSourceRow row, IReadOnlyDictionary<string, KitaronMappingField> fields, string key) =>
        fields.TryGetValue(key, out var field) ? Integer(Value(row, field)) : null;

    private static DateOnly? OptionalDate(KitaronSourceRow row, IReadOnlyDictionary<string, KitaronMappingField> fields, string key) =>
        fields.TryGetValue(key, out var field) ? Date(Value(row, field)) : null;

    private static int? OptionalSeconds(KitaronSourceRow row, IReadOnlyDictionary<string, KitaronMappingField> fields, string key)
    {
        if (!fields.TryGetValue(key, out var field)) return null;
        var value = Decimal(Value(row, field));
        if (value is null) return null;
        var multiplier = field.Transform switch
        {
            "seconds" or "positive_int" or "positive_integer" => 1m,
            "minutes_to_seconds" => 60m,
            "hours_to_seconds" => 3600m,
            _ => throw new KitaronSyncBlockedException($"Transform {field.Transform} is not executable for {key}.")
        };
        var result = decimal.Round(value.Value * multiplier, 0, MidpointRounding.AwayFromZero);
        return result is >= 0 and <= int.MaxValue ? (int)result : null;
    }

    private static object? Value(KitaronSourceRow row, KitaronMappingField field) =>
        field.SourceColumn is not null && row.Values.TryGetValue(field.SourceColumn, out var value) ? value : null;

    private static string? Text(KitaronSourceRow row, KitaronMappingField field)
    {
        var text = Convert.ToString(Value(row, field), CultureInfo.InvariantCulture)?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static decimal? Decimal(object? value)
    {
        if (value is null) return null;
        try { return Convert.ToDecimal(value, CultureInfo.InvariantCulture); }
        catch { return decimal.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out var result) ? result : null; }
    }

    private static int? Integer(object? value)
    {
        var number = Decimal(value);
        return number is >= 0 and <= int.MaxValue && decimal.Truncate(number.Value) == number.Value ? (int)number.Value : null;
    }

    private static DateOnly? Date(object? value)
    {
        if (value is DateTime dateTime) return DateOnly.FromDateTime(dateTime);
        if (value is DateTimeOffset offset) return DateOnly.FromDateTime(offset.DateTime);
        if (value is DateOnly date) return date;
        var text = Convert.ToString(value, CultureInfo.InvariantCulture);
        return DateOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed) ? parsed : null;
    }

    private static string? Consistent(IEnumerable<string?> values, string key, string field, ICollection<string> warnings)
    {
        var distinct = values.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (distinct.Length > 1) AddWarning(warnings, $"{key} has multiple {field} values; the first value was retained.");
        return distinct.FirstOrDefault();
    }

    private static void AddWarning(ICollection<string> warnings, string message)
    {
        if (warnings.Count < 500) warnings.Add(message);
    }

    private static string SafeFolder(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var safe = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "unknown" : safe[..Math.Min(safe.Length, 120)];
    }

    private static string Hash(params object?[] values) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(values)))).ToLowerInvariant();

    private static string SafeMessage(Exception exception)
    {
        var text = exception is KitaronSyncDataException ? exception.Message : "Kitaron synchronization failed. Review the Server log.";
        return text.Length <= 2000 ? text : text[..2000];
    }

    private sealed record ParsedRow(string Part, string Name, string? Revision, string? Customer,
        string? OrderNumber, int? Quantity, DateOnly? WorkFinishDate, int? OperationNumber,
        int? RoutePosition, string? OperationName, string? RequiredMachineType, int? SetupSeconds, int? CycleSeconds);

    private sealed record RawOperation(string SourceKey, string CaseSourceKey, int OperationNumber,
        int SourcePosition, string Name, string? RequiredMachineType, int? SetupSeconds, int? CycleSeconds);
}

internal sealed class KitaronSyncHostedService(
    IKitaronConnectionRepository connectionRepository,
    KitaronMappingService mappingService,
    KitaronSyncService syncService,
    TimeProvider timeProvider,
    ILogger<KitaronSyncHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var connection = await connectionRepository.GetAsync(stoppingToken);
                var mapping = await mappingService.GetAsync(stoppingToken);
                var status = await syncService.GetStatusAsync(stoppingToken);
                var due = status.LastCompletedAt is null
                    || timeProvider.GetUtcNow() - status.LastCompletedAt >= TimeSpan.FromSeconds(connection.RefreshIntervalSeconds);
                if (connection.Enabled && mapping.Status == "ready_for_implementation" && due)
                    await syncService.RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) { logger.LogError(exception, "Periodic Kitaron synchronization failed."); }
            await Task.Delay(TimeSpan.FromSeconds(30), timeProvider, stoppingToken);
        }
    }
}
