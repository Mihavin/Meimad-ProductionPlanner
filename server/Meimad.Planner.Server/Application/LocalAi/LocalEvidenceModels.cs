using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
namespace Meimad.Planner.Server.Application.LocalAi;

internal sealed record ExplanationRequest(string Intent, string? ProductionRunId = null,
    [property: JsonConverter(typeof(ExplicitInstantConverter))] DateTimeOffset? From = null,
    [property: JsonConverter(typeof(ExplicitInstantConverter))] DateTimeOffset? To = null, string? MachineId = null, string? Question = null,
    string? SearchTerm = null);
internal sealed record EvidenceLink(string EntityType, string EntityId);
internal sealed record EvidenceFact(string EvidenceId, string Kind, string Code, JsonElement Value,
    string? Unit, EvidenceLink Source, string? SourceVersion, DateTimeOffset? ObservedAt,
    DateTimeOffset? ReceivedAt, string Validity, string Description);
internal sealed record EvidenceClaim(string Code, string Text, IReadOnlyList<string> EvidenceIds);
internal sealed record EvidenceBundle(int SchemaVersion, string Intent, DateTimeOffset AsOfUtc,
    string PlanRevision, string? ProductionRunId, string? MachineId, DateTimeOffset? From, DateTimeOffset? To,
    IReadOnlyList<EvidenceFact> Facts, IReadOnlyList<EvidenceClaim> Claims, IReadOnlyList<string> KnownLimitations);
internal sealed record LocalExplanation(string RequestId, string Mode, EvidenceBundle Evidence,
    IReadOnlyList<EvidenceClaim> Claims, string? FallbackReason = null,
    string Currency = "SNAPSHOT", DateTimeOffset? CheckedAt = null);

internal sealed class ExplicitInstantConverter : JsonConverter<DateTimeOffset?>
{
    public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("Use an explicit UTC offset.");
        var text = reader.GetString()!;
        if (text.Length > 64 || !text.Contains('T') || !(text.EndsWith('Z') || text.LastIndexOf('+') > 10 || text.LastIndexOf('-') > 10)
            || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value))
            throw new JsonException("Use an instant with an explicit UTC offset; ambiguous local times must be resolved by the user.");
        return value.ToUniversalTime();
    }
    public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
    { if (value is null) writer.WriteNullValue(); else writer.WriteStringValue(value.Value.ToUniversalTime().ToString("O")); }
}
