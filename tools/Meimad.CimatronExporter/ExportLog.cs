using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Meimad.Planner.SimulationPackage.Diagnostics;
using Meimad.Planner.SimulationPackage.Manifest;

namespace Meimad.CimatronExporter;

internal sealed record ExportLogEntry(string Step, string Status, string? Code, string Message, string AtUtc);

/// <summary>
/// metadata/export-log.json of a package (and <c>last-export-log.json</c> beside the package, kept on
/// a partial failure): every step with a status and a stable code, the exporter/Cimatron/adapter
/// versions and the validator diagnostics.
/// </summary>
internal sealed class ExportLog(TimeProvider clock)
{
    private readonly List<ExportLogEntry> entries = [];
    private readonly List<PackageDiagnostic> diagnostics = [];

    public string ExporterVersion { get; init; } = typeof(ExportLog).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    public string? CimatronVersion { get; set; }
    public string? AdapterVersion { get; set; }
    public string StartedAtUtc { get; } = Stamp(clock);
    public string? FinishedAtUtc { get; private set; }
    public string Outcome { get; private set; } = "running";
    public string? Output { get; set; }
    public string? PackagePath { get; set; }
    public IReadOnlyDictionary<string, bool>? Capabilities { get; set; }

    public IReadOnlyList<ExportLogEntry> Entries => entries;
    public IReadOnlyList<PackageDiagnostic> Diagnostics => diagnostics;
    public bool HasErrors => entries.Any(entry => entry.Status == "error") || diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    public void Ok(string step, string message) => entries.Add(new(step, "ok", null, message, Stamp(clock)));
    public void Warning(string step, string code, string message) => entries.Add(new(step, "warning", code, message, Stamp(clock)));
    public void Error(string step, string code, string message) => entries.Add(new(step, "error", code, message, Stamp(clock)));
    public void Skipped(string step, string message) => entries.Add(new(step, "skipped", null, message, Stamp(clock)));
    public void AddDiagnostics(IEnumerable<PackageDiagnostic> items) => diagnostics.AddRange(items);

    public void Finish(bool complete)
    {
        Outcome = complete ? "complete" : "failed";
        FinishedAtUtc = Stamp(clock);
    }

    public string ToJson() => JsonSerializer.Serialize(new
    {
        exporter = new { name = "Meimad.CimatronExporter", version = ExporterVersion },
        cimatronVersion = CimatronVersion,
        adapterVersion = AdapterVersion,
        startedAtUtc = StartedAtUtc,
        finishedAtUtc = FinishedAtUtc,
        outcome = Outcome,
        output = Output,
        packagePath = PackagePath,
        capabilities = Capabilities,
        steps = entries,
        diagnostics
    }, Options);

    /// <summary>The human-readable report printed to the console and saved beside the package.</summary>
    public string ToReport()
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"Meimad Cimatron export — {(Outcome == "complete" ? "COMPLETE" : "FAILED, nothing was published")}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Exporter {ExporterVersion}, Cimatron {CimatronVersion ?? "unknown"}, adapter {AdapterVersion ?? "unknown"}");
        if (PackagePath is not null) text.AppendLine(CultureInfo.InvariantCulture, $"Package: {PackagePath}");
        var errors = entries.Where(entry => entry.Status == "error").Select(entry => $"{entry.Code}: {entry.Message}")
            .Concat(diagnostics.Where(item => item.Severity == DiagnosticSeverity.Error).Select(Describe)).ToArray();
        var warnings = entries.Where(entry => entry.Status == "warning").Select(entry => $"{entry.Code}: {entry.Message}")
            .Concat(diagnostics.Where(item => item.Severity == DiagnosticSeverity.Warning).Select(Describe)).ToArray();
        if (errors.Length > 0)
        {
            text.AppendLine("Errors (the package was not published):");
            foreach (var line in errors) text.AppendLine("  - " + line);
        }

        if (warnings.Length > 0)
        {
            text.AppendLine("Warnings:");
            foreach (var line in warnings) text.AppendLine("  - " + line);
        }

        text.AppendLine("Steps:");
        foreach (var entry in entries) text.AppendLine(CultureInfo.InvariantCulture, $"  [{entry.Status}] {entry.Step}: {entry.Message}");
        return text.ToString();
    }

    private static string Describe(PackageDiagnostic item) =>
        $"{item.Code}: {item.Message}" + (item.Context.Count == 0 ? "" : " (" + string.Join(", ", item.Context.Select(pair => $"{pair.Key}={pair.Value}")) + ")");

    private static string Stamp(TimeProvider clock) => clock.GetUtcNow().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private static readonly JsonSerializerOptions Options = new(ManifestJson.Options) { DefaultIgnoreCondition = JsonIgnoreCondition.Never };
}
