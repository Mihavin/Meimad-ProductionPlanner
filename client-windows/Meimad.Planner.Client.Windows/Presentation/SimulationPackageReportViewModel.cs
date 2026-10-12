using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Meimad.Planner.Client.Windows.Api;

namespace Meimad.Planner.Client.Windows.Presentation;

/// <summary>What the planner answered to a blocked import: the stock, the Machine and the confirmations.</summary>
internal sealed record SimulationPackageChoices(string? StockPath, string? MachineId, bool RememberMachineMapping, bool OverwriteManualWorkCs);

/// <summary>
/// The import report of a Cimatron simulation package: outcome, package facts, every diagnostic with its
/// severity as text and symbol (never color alone) and stable code, the files, and — when the Server
/// needs the planner — the stock and Machine candidates and the confirmations to import again.
/// </summary>
internal sealed class SimulationPackageReportViewModel : INotifyPropertyChanged
{
    private SimulationPackageStockOption? selectedStock;
    private SimulationPackageMachineOption? selectedMachine;
    private bool rememberMachineMapping;
    private bool overwriteManualWorkCs;

    internal SimulationPackageReportViewModel(SimulationPackageImportResult result, bool canManageSetup)
    {
        Result = result;
        var report = result.Report;
        var diagnostics = report.Diagnostics ?? [];
        Diagnostics = diagnostics
            .OrderBy(item => item.Severity switch { "error" => 0, "warning" => 1, _ => 2 })
            .Select(item => new SimulationPackageDiagnosticRow(item))
            .ToArray();
        Files = (report.Files ?? []).Select(file => new SimulationPackageFileRow(file)).ToArray();
        var codes = diagnostics.Select(item => item.Code).ToHashSet(StringComparer.Ordinal);
        var stockChoiceNeeded = codes.Contains("stock_selection_required") || codes.Contains("stock_selection_invalid");
        StockOptions = stockChoiceNeeded
            ? (report.StockCandidates ?? []).Select(path => new SimulationPackageStockOption(path, Files.FirstOrDefault(file => file.Path == path))).ToArray()
            : [];
        var machineChoiceNeeded = codes.Contains("machine_mapping_ambiguous") || codes.Contains("machine_mapping_unresolved")
            || codes.Contains("machine_selection_invalid");
        MachineOptions = machineChoiceNeeded
            ? (report.MachineCandidates ?? []).Select(candidate => new SimulationPackageMachineOption(candidate)).ToArray()
            : [];
        NeedsManualOverwrite = codes.Contains("work_cs_manual_conflict");
        CanRememberMachineMapping = machineChoiceNeeded && canManageSetup;
        var otherBlocking = diagnostics.Any(item => item.Severity == "error" && !Resolvable.Contains(item.Code));
        CanImportWithChoices = result.IsBlocked && !otherBlocking && (StockOptions.Count > 0 || MachineOptions.Count > 0 || NeedsManualOverwrite);
    }

    /// <summary>Blocking codes the planner can answer in this window; any other error needs a new export.</summary>
    internal static readonly IReadOnlySet<string> Resolvable = new HashSet<string>(StringComparer.Ordinal)
    {
        "stock_selection_required", "stock_selection_invalid", "machine_mapping_ambiguous", "machine_mapping_unresolved",
        "machine_selection_invalid", "work_cs_manual_conflict"
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    internal SimulationPackageImportResult Result { get; }

    public IReadOnlyList<SimulationPackageDiagnosticRow> Diagnostics { get; }

    public IReadOnlyList<SimulationPackageFileRow> Files { get; }

    public IReadOnlyList<SimulationPackageStockOption> StockOptions { get; }

    public IReadOnlyList<SimulationPackageMachineOption> MachineOptions { get; }

    public bool HasStockOptions => StockOptions.Count > 0;

    public bool HasMachineOptions => MachineOptions.Count > 0;

    public bool NeedsManualOverwrite { get; }

    public bool CanRememberMachineMapping { get; }

    public bool CanImportWithChoices { get; }

    public string OutcomeText => Result.Outcome switch
    {
        SimulationPackageImportResult.Imported => "✔ Imported",
        SimulationPackageImportResult.AlreadyImported => "✔ Already imported — nothing changed",
        _ => "⛔ Not imported"
    };

    public string OutcomeDetail => Result.Outcome switch
    {
        SimulationPackageImportResult.Imported => "The stock, the fixtures and the finished part are linked to the Operation and its Work CS is set from the package.",
        SimulationPackageImportResult.AlreadyImported => "This package is already the Operation's current package.",
        _ when CanImportWithChoices => "The Server needs your choice below before it can import the package. Nothing was changed.",
        _ => "The package has blocking problems. Nothing was changed; fix them in Cimatron and export again."
    };

    public string CountsText => string.Format(CultureInfo.InvariantCulture, "Errors: {0}, warnings: {1}, notes: {2}",
        Diagnostics.Count(row => row.Severity == "error"), Diagnostics.Count(row => row.Severity == "warning"),
        Diagnostics.Count(row => row.Severity == "info"));

    public string PackagePath => Result.Report.PackagePath;

    public string ExportText
    {
        get
        {
            var report = Result.Report;
            return $"Exporter {report.ExporterVersion ?? "?"}, Cimatron {report.CimatronVersion ?? "?"}, manifest schema {report.SchemaVersion?.ToString(CultureInfo.InvariantCulture) ?? "?"}";
        }
    }

    public string OperationText
    {
        get
        {
            var report = Result.Report;
            return $"OP{report.OperationNumber ?? "?"} {report.OperationName}  ·  procedure {report.ProcedureId?.ToString(CultureInfo.InvariantCulture) ?? "?"}";
        }
    }

    public string CoordinateText
    {
        get
        {
            var cs = Result.Report.CoordinateSystem;
            if (cs is null) return "No coordinate system";
            var mode = cs.Type == "nc-procedure-ucs"
                ? "files in NC work coordinates (identity)"
                : $"files in part-model coordinates, origin {Xyz(cs.OriginMm)} mm, rotation {Xyz(cs.RotationDeg)}° ({cs.RotationOrder})";
            return $"UCS {cs.Name}, {cs.WorkOffset ?? "no work offset"}: {mode}; units {Result.Report.Units}";
        }
    }

    public string MachineText
    {
        get
        {
            var report = Result.Report;
            var exported = report.Machine is null
                ? "no machine in the package"
                : $"{report.Machine.Name ?? "?"} / post {report.Machine.Postprocessor ?? "?"} / id {report.Machine.ExternalId ?? "?"}";
            var mapped = report.MachineId is null
                ? "not mapped"
                : $"Machine {(report.MachineCandidates ?? []).FirstOrDefault(item => item.MachineId == report.MachineId)?.Number ?? report.MachineId} ({report.MachineMatchedBy})";
            return $"{exported} → {mapped}";
        }
    }

    public string ToolsText
    {
        get
        {
            var report = Result.Report;
            var nc = report.Nc?.ToolNumbers ?? [];
            var table = report.ToolTableNumbers ?? [];
            return $"NC calls T{string.Join(", T", nc)}; the tool table lists T{string.Join(", T", table)}";
        }
    }

    public SimulationPackageStockOption? SelectedStock
    {
        get => selectedStock;
        set => SetField(ref selectedStock, value);
    }

    public SimulationPackageMachineOption? SelectedMachine
    {
        get => selectedMachine;
        set => SetField(ref selectedMachine, value);
    }

    public bool RememberMachineMapping
    {
        get => rememberMachineMapping;
        set => SetField(ref rememberMachineMapping, value);
    }

    public bool OverwriteManualWorkCs
    {
        get => overwriteManualWorkCs;
        set => SetField(ref overwriteManualWorkCs, value);
    }

    /// <summary>The planner's answers, or null with the reason when something is still missing.</summary>
    internal SimulationPackageChoices? Choices(out string? missing)
    {
        missing = null;
        if (HasStockOptions && SelectedStock is null) missing = "Choose the stock to start from.";
        else if (HasMachineOptions && SelectedMachine is null) missing = "Choose the Machine for this package.";
        else if (NeedsManualOverwrite && !OverwriteManualWorkCs) missing = "Confirm that the package Work CS replaces the manually entered one.";
        if (missing is not null) return null;
        return new SimulationPackageChoices(SelectedStock?.Path, SelectedMachine?.MachineId,
            CanRememberMachineMapping && RememberMachineMapping, OverwriteManualWorkCs);
    }

    private static string Xyz(PlannerXyz? value) => value is null
        ? "?"
        : string.Format(CultureInfo.InvariantCulture, "({0:0.###}, {1:0.###}, {2:0.###})", value.X, value.Y, value.Z);

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>One diagnostic: severity as text and symbol, the stable code, the message and its facts.</summary>
internal sealed class SimulationPackageDiagnosticRow(SimulationPackageDiagnostic diagnostic)
{
    public string Severity => diagnostic.Severity;
    public string SeverityText => diagnostic.Severity switch
    {
        "error" => "⛔ Error",
        "warning" => "⚠ Warning",
        _ => "ℹ Note"
    };
    public string Code => diagnostic.Code;
    public string Message => diagnostic.Message;
    public string Details => diagnostic.Context is null || diagnostic.Context.Count == 0
        ? string.Empty
        : string.Join("; ", diagnostic.Context.Select(pair => $"{pair.Key}: {pair.Value}"));
}

internal sealed class SimulationPackageFileRow(SimulationPackageReportFile file)
{
    public string Role => file.Role;
    public string RoleText => file.Role switch
    {
        "nc" => "NC program",
        "initialStock" => "Initial stock",
        "remainingStock" => string.IsNullOrWhiteSpace(file.SourceOperation) ? "Remaining stock" : $"Remaining stock from OP{file.SourceOperation}",
        "machinedStock" => "Machined stock",
        "fixture" => "Fixture",
        "part" => "Finished part",
        "toolTable" => "Tool table",
        "toolReport" => "Tool report",
        _ => file.Role
    };
    public string Path => file.Path;
    public string Name => file.Name ?? string.Empty;
    public string State => !file.Exists ? "✖ Missing" : file.Readable ? "✔ Read" : "✖ Unreadable";
    public string Size => file.SizeBytes is { } bytes
        ? (bytes >= 1024 * 1024
            ? (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB"
            : (bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB")
        : string.Empty;
    public string Triangles => file.TriangleCount?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
}

internal sealed class SimulationPackageStockOption(string path, SimulationPackageFileRow? file)
{
    public string Path { get; } = path;
    public string Label { get; } = file is null ? path : $"{file.RoleText}: {path}";
    public override string ToString() => Label;
}

internal sealed class SimulationPackageMachineOption(SimulationPackageMachineCandidate candidate)
{
    public string MachineId => candidate.MachineId;
    public string Label => $"{candidate.Number} — {candidate.Name}";
    public override string ToString() => Label;
}
