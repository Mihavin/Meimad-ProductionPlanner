using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Meimad.Planner.Server.Infrastructure.MtConnect;

internal static class MtConnectDocumentParser
{
    private const string DevicesNamespace = "urn:mtconnect.org:MTConnectDevices:1.2";
    private const string StreamsNamespace = "urn:mtconnect.org:MTConnectStreams:1.2";
    internal const int MaximumDocumentCharacters = 10 * 1024 * 1024;

    internal static MtConnectProbeDocument ParseProbe(string xml) =>
        ParseProbe(Load(xml), xml);

    internal static MtConnectCurrentDocument ParseCurrent(
        string xml,
        MtConnectProbeDocument? probe = null) =>
        ParseCurrent(Load(xml), xml, probe);

    internal static async Task<MtConnectProbeDocument> ParseProbeAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        var rawXml = await ReadBoundedAsync(stream, cancellationToken);
        return ParseProbe(Load(rawXml), rawXml);
    }

    internal static async Task<MtConnectCurrentDocument> ParseCurrentAsync(
        Stream stream,
        MtConnectProbeDocument? probe = null,
        CancellationToken cancellationToken = default)
    {
        var rawXml = await ReadBoundedAsync(stream, cancellationToken);
        return ParseCurrent(Load(rawXml), rawXml, probe);
    }

    private static MtConnectProbeDocument ParseProbe(XDocument document, string rawXml)
    {
        var root = RequireRoot(document, "MTConnectDevices", DevicesNamespace);
        var header = ParseHeader(root);
        var deviceElements = root.Descendants()
            .Where(element => element.Name.LocalName == "Device")
            .ToArray();
        var devices = deviceElements
            .Select(element => new MtConnectDeviceIdentity(
                Attribute(element, "id"),
                Attribute(element, "name"),
                Attribute(element, "uuid")))
            .ToArray();

        if (devices.Length == 0)
            throw new MtConnectProtocolException("The MTConnect probe document did not contain a Device.");

        var dataItems = deviceElements.SelectMany(device => device.Descendants()
                .Where(element => element.Name.LocalName == "DataItem")
                .Select(element => ParseDataItem(element, Attribute(device, "id"))))
            .ToArray();

        return new(header, devices, dataItems, rawXml);
    }

    private static MtConnectCurrentDocument ParseCurrent(
        XDocument document,
        string rawXml,
        MtConnectProbeDocument? probe)
    {
        var root = RequireRoot(document, "MTConnectStreams", StreamsNamespace);
        var header = ParseHeader(root);
        var definitions = (probe?.DataItems ?? [])
            .GroupBy(value => value.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var devices = root.Descendants()
            .Where(element => element.Name.LocalName == "DeviceStream")
            .Select(element => ParseDeviceState(element, definitions))
            .ToArray();

        if (devices.Length == 0)
            throw new MtConnectProtocolException("The MTConnect current document did not contain a DeviceStream.");

        return new(header, devices, rawXml);
    }

    private static MtConnectDataItemDefinition ParseDataItem(XElement element, string? deviceId)
    {
        var id = Attribute(element, "id")
            ?? throw new MtConnectProtocolException("An MTConnect DataItem did not contain an id.");
        return new(
            id,
            deviceId,
            Attribute(element, "name"),
            Attribute(element, "type"),
            Attribute(element, "subType"),
            Attribute(element, "category"),
            Attribute(element, "units"),
            element.Elements().FirstOrDefault(value => value.Name.LocalName == "Source")?.Value.Trim());
    }

    private static MtConnectDeviceState ParseDeviceState(
        XElement device,
        IReadOnlyDictionary<string, MtConnectDataItemDefinition> definitions)
    {
        var indexedObservations = device.Descendants()
            .Where(element => element.Attribute("dataItemId") is not null)
            .Select((element, index) => new IndexedObservation(
                ParseObservation(element, definitions), element, index))
            .ToArray();
        var latestObservations = indexedObservations
            .GroupBy(value => value.Observation.DataItemId, StringComparer.Ordinal)
            .Select(group => group
                .OrderBy(value => value.Observation.Sequence ?? long.MinValue)
                .ThenBy(value => value.Index)
                .Last())
            .OrderBy(value => value.Index)
            .ToArray();

        MtConnectObservation? Latest(string elementName) => latestObservations
            .Where(value => value.Observation.ElementName.Equals(elementName, StringComparison.Ordinal))
            .OrderBy(value => value.Observation.Sequence ?? long.MinValue)
            .ThenBy(value => value.Index)
            .Select(value => value.Observation)
            .LastOrDefault();

        var counters = latestObservations
            .Where(IsCounter)
            .OrderBy(value => value.Index)
            .Select(value => new MtConnectCounterObservation(
                value.Observation,
                ParseCounter(value.Observation.Value)))
            .ToArray();
        var macroVariables = latestObservations
            .SelectMany(ParseMacroRange)
            .ToArray();

        return new(
            new(Attribute(device, "id"), Attribute(device, "name"), Attribute(device, "uuid")),
            Latest("Availability"),
            Latest("Execution"),
            Latest("ControllerMode"),
            Latest("Program"),
            indexedObservations.Select(value => value.Observation).ToArray(),
            counters,
            macroVariables);
    }

    private static bool IsCounter(IndexedObservation value)
    {
        if (value.Observation.ElementName.Equals("PartCount", StringComparison.OrdinalIgnoreCase))
            return true;

        return value.Observation.Name?.Contains("Counter", StringComparison.OrdinalIgnoreCase) == true
            || value.Observation.Definition?.Source?.Contains("Counter", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static MtConnectObservation ParseObservation(
        XElement element,
        IReadOnlyDictionary<string, MtConnectDataItemDefinition> definitions)
    {
        var dataItemId = Attribute(element, "dataItemId")!;
        definitions.TryGetValue(dataItemId, out var definition);
        return new(
            element.Name.LocalName,
            dataItemId,
            Attribute(element, "name") ?? definition?.Name,
            element.Value.Trim(),
            ParseTimestamp(Attribute(element, "timestamp"), $"{element.Name.LocalName} timestamp"),
            ParseInteger(Attribute(element, "sequence"), $"{element.Name.LocalName} sequence"),
            element.Attributes()
                .Where(attribute => !attribute.IsNamespaceDeclaration)
                .GroupBy(attribute => attribute.Name.LocalName, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.Ordinal),
            definition);
    }

    private static IEnumerable<MtConnectMacroObservation> ParseMacroRange(IndexedObservation value)
    {
        if (!TryParseMacroRange(value.Observation.Definition?.Source, out var first, out var last))
            yield break;

        var values = value.Observation.Value.Split(',', StringSplitOptions.TrimEntries);
        var count = Math.Min(values.Length, last - first + 1);
        for (var index = 0; index < count; index++)
        {
            var raw = values[index];
            decimal? numeric = decimal.TryParse(raw, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
            yield return new(first + index, numeric, raw, value.Observation);
        }
    }

    private static bool TryParseMacroRange(string? source, out int first, out int last)
    {
        first = 0;
        last = 0;
        const string prefix = "Macros ";
        const string separator = " to ";
        if (source is null || !source.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        var range = source[prefix.Length..];
        var separatorIndex = range.IndexOf(separator, StringComparison.OrdinalIgnoreCase);
        if (separatorIndex < 1
            || !int.TryParse(range[..separatorIndex], NumberStyles.None,
                CultureInfo.InvariantCulture, out first)
            || !int.TryParse(range[(separatorIndex + separator.Length)..], NumberStyles.None,
                CultureInfo.InvariantCulture, out last)
            || first < 0 || last < first || last - first > 10_000)
        {
            first = 0;
            last = 0;
            return false;
        }
        return true;
    }

    private static long? ParseCounter(string value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            && parsed >= 0
                ? parsed
                : null;

    private static MtConnectHeader ParseHeader(XElement root)
    {
        var element = root.Elements().FirstOrDefault(value => value.Name.LocalName == "Header")
            ?? throw new MtConnectProtocolException("The MTConnect document did not contain a Header.");
        return new(
            ParseTimestamp(Attribute(element, "creationTime"), "Header creationTime"),
            Attribute(element, "sender"),
            Attribute(element, "instanceId"),
            Attribute(element, "version"),
            ParseInteger(Attribute(element, "bufferSize"), "Header bufferSize"),
            ParseInteger(Attribute(element, "firstSequence"), "Header firstSequence"),
            ParseInteger(Attribute(element, "lastSequence"), "Header lastSequence"),
            ParseInteger(Attribute(element, "nextSequence"), "Header nextSequence"));
    }

    private static XElement RequireRoot(XDocument document, string expectedName, string expectedNamespace)
    {
        var root = document.Root
            ?? throw new MtConnectProtocolException("The MTConnect XML document was empty.");
        if (root.Name.LocalName == "MTConnectError")
        {
            var error = root.Descendants().FirstOrDefault(value => value.Name.LocalName == "Error");
            var code = Attribute(error, "errorCode");
            var detail = error?.Value.Trim();
            var message = string.Join(": ", new[] { code, detail }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
            throw new MtConnectProtocolException(message.Length == 0
                ? "The MTConnect agent returned an error document."
                : $"The MTConnect agent returned {message}.");
        }

        if (root.Name.LocalName != expectedName || root.Name.NamespaceName != expectedNamespace)
        {
            throw new MtConnectProtocolException(
                $"Expected {expectedName} in MTConnect 1.2 namespace '{expectedNamespace}', " +
                $"but received '{root.Name}'.");
        }
        return root;
    }

    private static DateTimeOffset? ParseTimestamp(string? value, string field)
    {
        if (value is null) return null;
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            return parsed;
        }
        throw new MtConnectProtocolException($"The MTConnect {field} value '{value}' was invalid.");
    }

    private static long? ParseInteger(string? value, string field)
    {
        if (value is null) return null;
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            && parsed >= 0)
        {
            return parsed;
        }
        throw new MtConnectProtocolException($"The MTConnect {field} value '{value}' was invalid.");
    }

    private static string? Attribute(XElement? element, string name)
    {
        var value = element?.Attribute(name)?.Value.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static XDocument Load(string xml)
    {
        try
        {
            using var text = new StringReader(xml);
            using var reader = XmlReader.Create(text, ReaderSettings(async: false));
            return XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException exception)
        {
            throw new MtConnectProtocolException("The MTConnect response was not valid XML.", exception);
        }
    }

    private static async Task<string> ReadBoundedAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(
            stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
            bufferSize: 8192, leaveOpen: true);
        var result = new StringBuilder();
        var buffer = new char[8192];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (count == 0) return result.ToString();
            if (result.Length > MaximumDocumentCharacters - count)
                throw new MtConnectProtocolException(
                    $"The MTConnect response exceeded {MaximumDocumentCharacters} characters.");
            result.Append(buffer, 0, count);
        }
    }

    private static XmlReaderSettings ReaderSettings(bool async) => new()
    {
        Async = async,
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        MaxCharactersInDocument = MaximumDocumentCharacters
    };

    private sealed record IndexedObservation(
        MtConnectObservation Observation,
        XElement Element,
        int Index);
}
