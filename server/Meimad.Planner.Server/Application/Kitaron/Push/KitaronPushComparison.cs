using System.Security.Cryptography;
using System.Text.Json;

namespace Meimad.Planner.Server.Application.Kitaron.Push;

internal static class KitaronPushComparison
{
    // Compare provider values exactly, not the planner's display/write tolerance.
    // Reads on the same SQL columns preserve numeric types/scale and datetime ticks.
    internal static bool Matches(KitaronOperationRow expected, KitaronOperationRow? current) =>
        current is not null && !current.WorkOrderClosed && !expected.WorkOrderClosed
        && expected.RowId == current.RowId && expected.WorkOrderNumber == current.WorkOrderNumber
        && string.Equals(expected.ActionNumber, current.ActionNumber, StringComparison.Ordinal)
        && expected.Values.All(pair => current.Values.TryGetValue(pair.Key, out var value)
            && Equals(Normalize(pair.Value), Normalize(value)));

    private static object? Normalize(object? value) => value is DBNull ? null : value;

    internal static string Stamp(KitaronPushSettings settings, KitaronPushPlanner.Plan plan,
        StoredKitaronConnectionSettings? connection = null) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            settings.Version,
            Target = connection is null ? null : new { connection.ServerHost, connection.ServerPort, connection.DatabaseName, connection.Version },
            Writes = plan.Writes.OrderBy(x => x.WorkOrderNumber).ThenBy(x => x.RowId)
                .ThenBy(x => x.Column, StringComparer.Ordinal).Select(x => new
                {
                    x.RowId, x.WorkOrderNumber, x.Column, x.Expected.ActionNumber,
                    Values = x.Expected.Values.OrderBy(v => v.Key, StringComparer.Ordinal)
                        .Select(v => new { v.Key, Value = Normalize(v.Value) })
                })
        })));

    internal static KitaronPushConflictException Conflict(long rowId) => new(
        $"Kitaron operation row {rowId} changed, disappeared, or its Work Order closed/stopped. "
        + "The whole push was refused. Refresh Preview and review the current values before pushing again.");
}
