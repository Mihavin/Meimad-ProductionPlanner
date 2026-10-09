using System.Globalization;
using Meimad.Planner.Server.Application.Kitaron;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

// Preview executes the same domain rules under rollback; this inventory only explains the resulting changes.
internal static class KitaronImportDiff
{
    private static readonly string[] Tables = ["cases", "orders", "case_operations", "case_components",
        "operation_resource_requirements", "production_batches", "batch_operations", "batch_allocations",
        "kitaron_material_orders", "work_order_material_orders", "kitaron_sync_links"];
    private static readonly HashSet<string> Bookkeeping = new(StringComparer.Ordinal)
        { "created_at", "updated_at", "version", "first_seen_at", "last_seen_at", "first_imported_at", "last_imported_at" };
    internal sealed record Row(string Table, string Key, IReadOnlyDictionary<string, string?> Fields);

    internal static async Task<Dictionary<string, Row>> ReadAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        var result = new Dictionary<string, Row>(StringComparer.Ordinal);
        foreach (var table in Tables)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"SELECT * FROM {table}";
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var fields = new SortedDictionary<string, string?>(StringComparer.Ordinal);
                for (var i = 0; i < reader.FieldCount; i++)
                    if (!Bookkeeping.Contains(reader.GetName(i))) fields[reader.GetName(i)] = reader.IsDBNull(i) ? null
                        : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);
                var key = fields.GetValueOrDefault("id") ?? (table switch
                {
                    "kitaron_sync_links" => fields["source_entity"] + ":" + fields["source_key"],
                    "kitaron_material_orders" => fields["source_key"]!,
                    "work_order_material_orders" => fields["production_batch_id"] + ":" + fields["material_order_source_key"],
                    _ => KitaronImportFingerprint.Of(fields)
                });
                result.Add(table + ":" + key, new(table, key, fields));
                if (result.Count > 1_000_000) throw new KitaronSyncBlockedException("Import preview exceeds one million local rows; narrow the import before review.");
            }
        }
        return result;
    }

    internal static KitaronImportLocalPreview Compare(Dictionary<string, Row> before, Dictionary<string, Row> after, KitaronSyncStatus status)
    {
        var changes = new List<KitaronImportChange>();
        var total = 0;
        var unchanged = 0;
        var destructive = false;
        foreach (var key in before.Keys.Union(after.Keys).OrderBy(x => x, StringComparer.Ordinal))
        {
            before.TryGetValue(key, out var old);
            after.TryGetValue(key, out var next);
            var fields = (old?.Fields.Keys ?? []).Union(next?.Fields.Keys ?? []).OrderBy(x => x, StringComparer.Ordinal)
                .Where(name => old?.Fields.GetValueOrDefault(name) != next?.Fields.GetValueOrDefault(name))
                .Select(name => new KitaronImportFieldChange(name, Display(old?.Fields.GetValueOrDefault(name)), Display(next?.Fields.GetValueOrDefault(name)))).ToArray();
            if (old is not null && next is not null && fields.Length == 0) { unchanged++; continue; }
            total++;
            destructive |= next is null || old is not null && fields.Any(f => f.Field is "is_active" or "status" && f.Before != f.After);
            if (changes.Count < 500) changes.Add(new((next ?? old)!.Table, (next ?? old)!.Key,
                old is null ? "create" : next is null ? "delete" : "update", fields));
        }
        return new(status, changes, total, unchanged, total > changes.Count, destructive);
    }
    private static string? Display(string? value) => value is { Length: > 256 } ? value[..256] + "..." : value;
}
