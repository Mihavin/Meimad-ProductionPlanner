using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Meimad.Planner.SimulationPackage.Schema;

/// <summary>One schema violation: the JSON pointer of the offending value, the keyword and a message.</summary>
public sealed record JsonSchemaViolation(string InstancePointer, string Keyword, string Message);

/// <summary>
/// Validates JSON documents against the package schemas shipped with this library. It implements the
/// JSON Schema (2020-12) keywords those schemas use and nothing else: <c>type</c>, <c>const</c>,
/// <c>enum</c>, <c>properties</c>, <c>required</c>, <c>additionalProperties</c>, <c>items</c>,
/// <c>minItems</c>/<c>maxItems</c>, <c>minLength</c>/<c>maxLength</c>, <c>pattern</c>,
/// <c>minimum</c>/<c>maximum</c>, <c>oneOf</c>/<c>anyOf</c>/<c>allOf</c> and <c>$ref</c> to a
/// <c>$defs</c> entry of the same or a sibling schema file. A schema that uses any other keyword is
/// rejected when it is loaded, so the subset can never silently ignore part of the contract.
/// </summary>
public sealed class JsonSchemaSubsetValidator
{
    public const string SimulationPackageSchema = "simulation-package.schema.json";
    public const string ToolTableSchema = "tool-table.schema.json";
    public const string CoordinateSystemsSchema = "coordinate-systems.schema.json";

    private static readonly HashSet<string> SupportedKeywords = new(StringComparer.Ordinal)
    {
        "$schema", "$id", "$defs", "$ref", "title", "description", "type", "const", "enum", "properties",
        "required", "additionalProperties", "items", "minItems", "maxItems", "minLength", "maxLength",
        "pattern", "minimum", "maximum", "oneOf", "anyOf", "allOf"
    };

    private static readonly Lazy<JsonSchemaSubsetValidator> Shared = new(LoadEmbedded);
    private readonly Dictionary<string, JsonElement> documents;
    private readonly Dictionary<string, Regex> patterns = new(StringComparer.Ordinal);

    private JsonSchemaSubsetValidator(Dictionary<string, JsonElement> documents)
    {
        this.documents = documents;
        foreach (var (name, root) in documents) CheckKeywords(name, root, "#");
    }

    /// <summary>The validator over the three schemas embedded in this assembly.</summary>
    public static JsonSchemaSubsetValidator Default => Shared.Value;

    /// <summary>The schema file names this validator knows.</summary>
    public IReadOnlyCollection<string> SchemaNames => documents.Keys;

    /// <summary>Returns the raw text of an embedded schema (for tools that publish the contract).</summary>
    public static string ReadEmbeddedSchemaText(string schemaName)
    {
        using var stream = typeof(JsonSchemaSubsetValidator).Assembly
            .GetManifestResourceStream("Meimad.Planner.SimulationPackage.Schemas." + schemaName)
            ?? throw new ArgumentException($"Unknown schema '{schemaName}'.", nameof(schemaName));
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Builds a validator from schema texts keyed by file name (tests use this for broken schemas).</summary>
    public static JsonSchemaSubsetValidator FromTexts(IReadOnlyDictionary<string, string> schemas)
    {
        var documents = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (name, text) in schemas)
        {
            using var document = JsonDocument.Parse(text);
            documents[name] = document.RootElement.Clone();
        }

        return new JsonSchemaSubsetValidator(documents);
    }

    public IReadOnlyList<JsonSchemaViolation> Validate(string schemaName, JsonElement instance)
    {
        if (!documents.TryGetValue(schemaName, out var root))
            throw new ArgumentException($"Unknown schema '{schemaName}'.", nameof(schemaName));
        var violations = new List<JsonSchemaViolation>();
        Validate(schemaName, root, instance, "", violations);
        return violations;
    }

    private static JsonSchemaSubsetValidator LoadEmbedded()
    {
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in new[] { SimulationPackageSchema, ToolTableSchema, CoordinateSystemsSchema })
            texts[name] = ReadEmbeddedSchemaText(name);
        return FromTexts(texts);
    }

    private static void CheckKeywords(string document, JsonElement schema, string pointer)
    {
        if (schema.ValueKind != JsonValueKind.Object) return;
        foreach (var property in schema.EnumerateObject())
        {
            if (!SupportedKeywords.Contains(property.Name))
                throw new InvalidOperationException($"Schema {document}{pointer} uses unsupported keyword '{property.Name}'.");
            switch (property.Name)
            {
                case "properties":
                case "$defs":
                    foreach (var child in property.Value.EnumerateObject())
                        CheckKeywords(document, child.Value, $"{pointer}/{property.Name}/{child.Name}");
                    break;
                case "items":
                case "additionalProperties":
                    CheckKeywords(document, property.Value, $"{pointer}/{property.Name}");
                    break;
                case "oneOf":
                case "anyOf":
                case "allOf":
                    var index = 0;
                    foreach (var child in property.Value.EnumerateArray())
                        CheckKeywords(document, child, $"{pointer}/{property.Name}/{index++}");
                    break;
            }
        }
    }

    private void Validate(string document, JsonElement schema, JsonElement instance, string pointer, List<JsonSchemaViolation> violations)
    {
        if (schema.ValueKind == JsonValueKind.True) return;
        if (schema.ValueKind == JsonValueKind.False)
        {
            violations.Add(new(pointer, "false", "No value is allowed here."));
            return;
        }

        if (schema.TryGetProperty("$ref", out var reference))
        {
            var (targetDocument, target) = Resolve(document, reference.GetString()!);
            Validate(targetDocument, target, instance, pointer, violations);
        }

        if (schema.TryGetProperty("type", out var type) && !MatchesType(type, instance))
        {
            violations.Add(new(pointer, "type", $"Expected {TypeText(type)}, found {Describe(instance)}."));
            return;
        }

        if (schema.TryGetProperty("const", out var constant) && !JsonEquals(constant, instance))
            violations.Add(new(pointer, "const", $"Expected the value {constant.GetRawText()}."));

        if (schema.TryGetProperty("enum", out var allowed)
            && !allowed.EnumerateArray().Any(item => JsonEquals(item, instance)))
        {
            violations.Add(new(pointer, "enum",
                $"Expected one of {string.Join(", ", allowed.EnumerateArray().Select(item => item.GetRawText()))}."));
        }

        switch (instance.ValueKind)
        {
            case JsonValueKind.Object:
                ValidateObject(document, schema, instance, pointer, violations);
                break;
            case JsonValueKind.Array:
                ValidateArray(document, schema, instance, pointer, violations);
                break;
            case JsonValueKind.String:
                ValidateString(schema, instance.GetString()!, pointer, violations);
                break;
            case JsonValueKind.Number:
                ValidateNumber(schema, instance.GetDouble(), pointer, violations);
                break;
        }

        if (schema.TryGetProperty("allOf", out var allOf))
        {
            foreach (var branch in allOf.EnumerateArray()) Validate(document, branch, instance, pointer, violations);
        }

        if (schema.TryGetProperty("anyOf", out var anyOf))
        {
            var branches = anyOf.EnumerateArray().Select(branch => Probe(document, branch, instance, pointer)).ToArray();
            if (branches.All(result => result.Count > 0))
            {
                violations.Add(new(pointer, "anyOf", "The value matches none of the allowed forms: "
                    + string.Join(" | ", branches.Select(result => result[0].Message))));
            }
        }

        if (schema.TryGetProperty("oneOf", out var oneOf))
        {
            var branches = oneOf.EnumerateArray().Select(branch => Probe(document, branch, instance, pointer)).ToArray();
            var matching = branches.Count(result => result.Count == 0);
            if (matching == 0)
            {
                // Report the branch that came closest, so the message names the real problem.
                var closest = branches.OrderBy(result => result.Count).First();
                violations.AddRange(closest);
            }
            else if (matching > 1)
            {
                violations.Add(new(pointer, "oneOf", "The value matches more than one of the exclusive forms."));
            }
        }
    }

    private List<JsonSchemaViolation> Probe(string document, JsonElement schema, JsonElement instance, string pointer)
    {
        var result = new List<JsonSchemaViolation>();
        Validate(document, schema, instance, pointer, result);
        return result;
    }

    private void ValidateObject(string document, JsonElement schema, JsonElement instance, string pointer, List<JsonSchemaViolation> violations)
    {
        if (schema.TryGetProperty("required", out var required))
        {
            foreach (var name in required.EnumerateArray().Select(item => item.GetString()!))
            {
                if (!instance.TryGetProperty(name, out _))
                    violations.Add(new($"{pointer}/{Escape(name)}", "required", $"The property '{name}' is required."));
            }
        }

        schema.TryGetProperty("properties", out var properties);
        var hasAdditional = schema.TryGetProperty("additionalProperties", out var additional);
        foreach (var property in instance.EnumerateObject())
        {
            var childPointer = $"{pointer}/{Escape(property.Name)}";
            if (properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(property.Name, out var childSchema))
            {
                Validate(document, childSchema, property.Value, childPointer, violations);
            }
            else if (hasAdditional)
            {
                if (additional.ValueKind == JsonValueKind.False)
                    violations.Add(new(childPointer, "additionalProperties", $"The property '{property.Name}' is not part of the contract."));
                else
                    Validate(document, additional, property.Value, childPointer, violations);
            }
        }
    }

    private void ValidateArray(string document, JsonElement schema, JsonElement instance, string pointer, List<JsonSchemaViolation> violations)
    {
        var count = instance.GetArrayLength();
        if (schema.TryGetProperty("minItems", out var minItems) && count < minItems.GetInt32())
            violations.Add(new(pointer, "minItems", $"Expected at least {minItems.GetInt32()} items, found {count}."));
        if (schema.TryGetProperty("maxItems", out var maxItems) && count > maxItems.GetInt32())
            violations.Add(new(pointer, "maxItems", $"Expected at most {maxItems.GetInt32()} items, found {count}."));
        if (!schema.TryGetProperty("items", out var items)) return;
        var index = 0;
        foreach (var item in instance.EnumerateArray())
            Validate(document, items, item, $"{pointer}/{index++}", violations);
    }

    private void ValidateString(JsonElement schema, string value, string pointer, List<JsonSchemaViolation> violations)
    {
        var length = new StringInfo(value).LengthInTextElements;
        if (schema.TryGetProperty("minLength", out var minLength) && length < minLength.GetInt32())
            violations.Add(new(pointer, "minLength", $"Expected at least {minLength.GetInt32()} characters."));
        if (schema.TryGetProperty("maxLength", out var maxLength) && length > maxLength.GetInt32())
            violations.Add(new(pointer, "maxLength", $"Expected at most {maxLength.GetInt32()} characters."));
        if (schema.TryGetProperty("pattern", out var pattern))
        {
            var text = pattern.GetString()!;
            if (!patterns.TryGetValue(text, out var regex))
            {
                regex = new Regex(text, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                patterns[text] = regex;
            }

            if (!regex.IsMatch(value))
                violations.Add(new(pointer, "pattern", "The value does not have the required form."));
        }
    }

    private static void ValidateNumber(JsonElement schema, double value, string pointer, List<JsonSchemaViolation> violations)
    {
        if (schema.TryGetProperty("minimum", out var minimum) && value < minimum.GetDouble())
            violations.Add(new(pointer, "minimum", $"Expected at least {minimum.GetRawText()}."));
        if (schema.TryGetProperty("maximum", out var maximum) && value > maximum.GetDouble())
            violations.Add(new(pointer, "maximum", $"Expected at most {maximum.GetRawText()}."));
    }

    private (string Document, JsonElement Schema) Resolve(string currentDocument, string reference)
    {
        var hash = reference.IndexOf('#', StringComparison.Ordinal);
        var file = hash <= 0 ? (hash == 0 ? currentDocument : reference) : reference[..hash];
        var fragment = hash < 0 ? "" : reference[(hash + 1)..];
        if (!documents.TryGetValue(file, out var target))
            throw new InvalidOperationException($"Schema reference '{reference}' names an unknown schema file.");
        foreach (var segment in fragment.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (target.ValueKind != JsonValueKind.Object || !target.TryGetProperty(name, out target))
                throw new InvalidOperationException($"Schema reference '{reference}' cannot be resolved.");
        }

        return (file, target);
    }

    private static bool MatchesType(JsonElement type, JsonElement instance) =>
        type.ValueKind == JsonValueKind.Array
            ? type.EnumerateArray().Any(item => MatchesType(item.GetString()!, instance))
            : MatchesType(type.GetString()!, instance);

    private static bool MatchesType(string type, JsonElement instance) => type switch
    {
        "object" => instance.ValueKind == JsonValueKind.Object,
        "array" => instance.ValueKind == JsonValueKind.Array,
        "string" => instance.ValueKind == JsonValueKind.String,
        "number" => instance.ValueKind == JsonValueKind.Number,
        "integer" => instance.ValueKind == JsonValueKind.Number && IsInteger(instance),
        "boolean" => instance.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => instance.ValueKind == JsonValueKind.Null,
        _ => throw new InvalidOperationException($"Unsupported schema type '{type}'.")
    };

    private static bool IsInteger(JsonElement value) =>
        value.TryGetInt64(out _) || (value.TryGetDouble(out var number) && Math.Floor(number) == number && !double.IsInfinity(number));

    private static string TypeText(JsonElement type) =>
        type.ValueKind == JsonValueKind.Array
            ? string.Join(" or ", type.EnumerateArray().Select(item => item.GetString()))
            : type.GetString()!;

    private static string Describe(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Null => "null",
        _ => value.ValueKind.ToString()
    };

    private static bool JsonEquals(JsonElement left, JsonElement right)
    {
        if (left.ValueKind == JsonValueKind.Number && right.ValueKind == JsonValueKind.Number)
            return left.GetDouble().Equals(right.GetDouble());
        if (left.ValueKind != right.ValueKind) return false;
        return left.ValueKind switch
        {
            JsonValueKind.String => left.GetString() == right.GetString(),
            JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => true,
            JsonValueKind.Array => left.GetArrayLength() == right.GetArrayLength()
                && left.EnumerateArray().Zip(right.EnumerateArray()).All(pair => JsonEquals(pair.First, pair.Second)),
            JsonValueKind.Object => left.EnumerateObject().Count() == right.EnumerateObject().Count()
                && left.EnumerateObject().All(property => right.TryGetProperty(property.Name, out var other) && JsonEquals(property.Value, other)),
            _ => false
        };
    }

    private static string Escape(string name) =>
        name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
}
