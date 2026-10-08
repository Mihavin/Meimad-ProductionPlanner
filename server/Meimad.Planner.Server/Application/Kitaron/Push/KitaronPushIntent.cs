using System.Globalization;

namespace Meimad.Planner.Server.Application.Kitaron.Push;

// Explicit tagged values retain provider precision/types through JSON; no JsonElement comparison.
internal sealed record KitaronStoredValue(string Kind, string? Text)
{
    internal static KitaronStoredValue From(object? value) => value switch
    {
        null or DBNull => new("null", null),
        DateTime d => new("datetime", d.ToString("O", CultureInfo.InvariantCulture)),
        decimal n => new("decimal", n.ToString(CultureInfo.InvariantCulture)),
        double n => new("double", n.ToString("R", CultureInfo.InvariantCulture)),
        float n => new("float", n.ToString("R", CultureInfo.InvariantCulture)),
        int n => new("int", n.ToString(CultureInfo.InvariantCulture)),
        long n => new("long", n.ToString(CultureInfo.InvariantCulture)),
        short n => new("short", n.ToString(CultureInfo.InvariantCulture)),
        byte n => new("byte", n.ToString(CultureInfo.InvariantCulture)),
        string s => new("string", s),
        _ => throw new InvalidOperationException("Unsupported ERP comparison value type.")
    };

    internal object? Value() => Kind switch
    {
        "null" => null,
        "datetime" => DateTime.Parse(Text!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        "decimal" => decimal.Parse(Text!, CultureInfo.InvariantCulture),
        "double" => double.Parse(Text!, CultureInfo.InvariantCulture),
        "float" => float.Parse(Text!, CultureInfo.InvariantCulture),
        "int" => int.Parse(Text!, CultureInfo.InvariantCulture),
        "long" => long.Parse(Text!, CultureInfo.InvariantCulture),
        "short" => short.Parse(Text!, CultureInfo.InvariantCulture),
        "byte" => byte.Parse(Text!, CultureInfo.InvariantCulture),
        "string" => Text,
        _ => throw new InvalidOperationException("Unsupported stored ERP comparison value type.")
    };
}

internal sealed record KitaronIntentWrite(long RowId, int WorkOrderNumber, string ActionNumber, string Column,
    KitaronStoredValue Value, IReadOnlyDictionary<string, KitaronStoredValue> ExpectedValues)
{
    internal static KitaronIntentWrite From(KitaronPushWrite write) => new(write.RowId, write.WorkOrderNumber,
        write.Expected.ActionNumber, write.Column, KitaronStoredValue.From(write.Value),
        write.Expected.Values.ToDictionary(x => x.Key, x => KitaronStoredValue.From(x.Value)));
}

internal sealed record KitaronPushIntent(string ServerHost, int ServerPort, string DatabaseName, int ConnectionVersion,
    KitaronPushSettings Settings, IReadOnlyList<KitaronPushOperation> Operations,
    IReadOnlyDictionary<string, KitaronPushForecast> Forecasts, IReadOnlyList<KitaronIntentWrite> Writes,
    IReadOnlyList<KitaronPushChange> Changes, string PreviewStamp);

internal sealed record KitaronReconciliationValue(long RowId, string Column, string Comparison, KitaronStoredValue? Current);
internal sealed record KitaronReconciliation(string Actor, DateTimeOffset ObservedAt,
    IReadOnlyList<KitaronReconciliationValue> Values, string Message, string Kind = "Observation");
internal sealed record KitaronPushIntentResource(string RunId, string State, int Version,
    KitaronPushIntent? Intent, IReadOnlyList<KitaronReconciliation> Reconciliations);

internal sealed class KitaronPushNotCommittedException(string message, Exception? inner = null) : Exception(message, inner);
internal sealed class KitaronPushOutcomeUnknownException(string runId) : KitaronPushBlockedException(
    $"Push {runId}: the ERP outcome is unknown. Do not retry. Inspect and reconcile this run before another push.")
{
    internal string RunId { get; } = runId;
}
