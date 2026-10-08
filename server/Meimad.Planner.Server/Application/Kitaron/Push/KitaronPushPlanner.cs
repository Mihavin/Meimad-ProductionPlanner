using System.Globalization;

namespace Meimad.Planner.Server.Application.Kitaron.Push;

/// <summary>
/// Decides which Kitaron values change: a Planner operation matches the one <c>TSubRootCard</c> row of
/// its Work Order (<c>NUMBER</c>) and operation number (<c>ActionNumber</c>); each switched-on mapping
/// writes the Planner value when the Planner has one and it differs from Kitaron's. The Planner's value
/// replaces Kitaron's (owner decision 2026-09-28); a missing Planner value never clears Kitaron's.
/// Kitaron keeps factory-local wall-clock times, so Planner instants are converted to the factory zone.
/// </summary>
internal static class KitaronPushPlanner
{
    internal sealed record Plan(
        IReadOnlyList<KitaronPushWrite> Writes,
        IReadOnlyList<KitaronPushChange> Changes,
        int OperationsMatched,
        int OperationsSkipped,
        IReadOnlyList<string> Notes);

    internal static Plan Build(
        IReadOnlyList<KitaronPushMapping> mappings,
        IReadOnlyList<KitaronPushOperation> operations,
        IReadOnlyDictionary<string, KitaronPushForecast> forecasts,
        IReadOnlyList<KitaronOperationRow> rows,
        TimeZoneInfo factoryZone)
    {
        var active = mappings.Where(mapping => mapping.Enabled).ToArray();
        var notes = new List<string>();
        if (active.Length == 0)
        {
            notes.Add("No column is switched on, so nothing is pushed.");
            return new Plan([], [], 0, 0, notes);
        }

        var byOperation = rows
            .GroupBy(row => (row.WorkOrderNumber, OperationNumber: ParseActionNumber(row.ActionNumber)))
            .ToDictionary(group => group.Key, group => group.ToArray());
        var writes = new List<KitaronPushWrite>();
        var changes = new List<KitaronPushChange>();
        int matched = 0, missing = 0, ambiguous = 0, closed = 0;
        foreach (var operation in operations
                     .OrderBy(value => value.WorkOrderNumber)
                     .ThenBy(value => value.OperationNumber))
        {
            if (!byOperation.TryGetValue((operation.WorkOrderNumber, operation.OperationNumber), out var candidates))
            {
                missing++;
                continue;
            }
            if (candidates.Length > 1)
            {
                ambiguous++;
                continue;
            }
            var row = candidates[0];
            if (row.WorkOrderClosed)
            {
                closed++;
                continue;
            }

            matched++;
            foreach (var mapping in active)
            {
                var value = Value(mapping.PlannerValue, operation, forecasts, factoryZone);
                if (value is null) continue;
                row.Values.TryGetValue(mapping.KitaronColumn, out var current);
                if (Same(current, value)) continue;
                writes.Add(new KitaronPushWrite(row.RowId, row.WorkOrderNumber, mapping.KitaronColumn, value, row with
                { Values = new Dictionary<string, object?>(row.Values, StringComparer.OrdinalIgnoreCase) }));
                changes.Add(new KitaronPushChange(
                    row.WorkOrderNumber, row.ActionNumber.Trim(), row.RowId, operation.PartNumber, operation.OperationName,
                    mapping.KitaronColumn, Format(current), Format(value)!));
            }
        }

        if (missing > 0)
            notes.Add($"{missing} operations have no Kitaron operation with the same Work Order and operation number.");
        if (ambiguous > 0)
            notes.Add($"{ambiguous} operations match more than one Kitaron operation and were left alone.");
        if (closed > 0)
            notes.Add($"{closed} operations belong to Work Orders that are closed or stopped in Kitaron and were left alone.");
        return new Plan(writes, changes, matched, missing + ambiguous + closed, notes);
    }

    /// <summary>The Planner value of <paramref name="code"/>, as Kitaron stores it; null when the Planner has none.</summary>
    internal static object? Value(
        string code,
        KitaronPushOperation operation,
        IReadOnlyDictionary<string, KitaronPushForecast> forecasts,
        TimeZoneInfo factoryZone)
    {
        forecasts.TryGetValue(operation.BatchOperationId, out var forecast);
        return code switch
        {
            "actual_start" => Local(operation.ActualStart, factoryZone),
            "actual_finish" => Local(operation.ActualEnd, factoryZone),
            "forecast_start" => Local(forecast?.StartsAt, factoryZone),
            "forecast_finish" => Local(forecast?.EndsAt, factoryZone),
            "setup_minutes" => operation.ActualStart is { } start
                               && operation.FirstQcPassAt is { } passed
                               && passed > start
                ? Math.Round((passed - start).TotalMinutes, 2)
                : null,
            "good_quantity" => operation.GoodQuantity > 0 ? (double)operation.GoodQuantity : null,
            "planned_quantity" => operation.PlannedQuantity > 0 ? (double)operation.PlannedQuantity : null,
            _ => null
        };
    }

    internal static int? ParseActionNumber(string actionNumber) =>
        int.TryParse(actionNumber.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;

    internal static string? Format(object? value) => value switch
    {
        null or DBNull => null,
        DateTime date => date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        double number => number.ToString("0.##", CultureInfo.InvariantCulture),
        float number => ((double)number).ToString("0.##", CultureInfo.InvariantCulture),
        IFormattable other => other.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString()
    };

    private static DateTime? Local(DateTimeOffset? instant, TimeZoneInfo zone)
    {
        if (instant is not { } value) return null;
        var local = TimeZoneInfo.ConvertTime(value, zone).DateTime;
        // Kitaron's datetime keeps about 3 ms; whole seconds compare and read back cleanly.
        return DateTime.SpecifyKind(new DateTime(local.Ticks - local.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Unspecified);
    }

    private static bool Same(object? current, object value) => (current, value) switch
    {
        (DateTime left, DateTime right) => Math.Abs((left - right).TotalSeconds) < 1,
        (double left, double right) => Math.Abs(left - right) < 0.005,
        (float left, double right) => Math.Abs(left - right) < 0.005,
        (IConvertible left, double right) when current is not (string or DateTime)
            => Math.Abs(left.ToDouble(CultureInfo.InvariantCulture) - right) < 0.005,
        _ => false
    };
}
