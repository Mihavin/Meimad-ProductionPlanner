using System.Globalization;

namespace Meimad.CimatronExporter;

/// <summary>Command line of the exporter (exporter definition §12 C plus the options the fallbacks need).</summary>
internal sealed record ExportOptions
{
    public bool ActiveCimatron { get; init; }
    public bool Probe { get; init; }
    /// <summary>Register (or remove) Cimatron's type libraries (current user, or every user from an administrator window), then exit.</summary>
    public bool RegisterCimatron { get; init; }
    public bool UnregisterCimatron { get; init; }
    /// <summary>The Cimatron automation ProgID when it is not CimAppAccess.AppAccess (also MEIMAD_CIMATRON_PROGID).</summary>
    public string? ProgId { get; init; }
    /// <summary>The Cimatron Program folder when CimAppAccess is not registered (default: the newest installed Cimatron).</summary>
    public string? CimatronFolder { get; init; }
    public string? Output { get; init; }
    public string PackageFolderName { get; init; } = "cimatron-export";
    public string? Post { get; init; }
    public string? Ucs { get; init; }
    public string? WorkOffset { get; init; }
    public bool StartHome { get; init; }
    public string? OperationNumber { get; init; }
    public string? OperationName { get; init; }
    public int? ProcedureId { get; init; }

    /// <summary>The Cimatron toolpath to export (one package per toolpath); required when the document has several.</summary>
    public string? Toolpath { get; init; }
    public string? PartNumber { get; init; }
    public string? PartName { get; init; }
    public string? MachineExternalId { get; init; }
    public string? MachineName { get; init; }
    public string? AxisConfiguration { get; init; }

    /// <summary>initial | remaining | auto (remaining when a source procedure is given).</summary>
    public string Stock { get; init; } = "auto";
    public int? StockProcedureId { get; init; }
    public string? SourceOperation { get; init; }

    /// <summary>A stock STL Cimatron already wrote (e.g. NC\STOCK_OP1.stl); copied, recorded as found by fallback.</summary>
    public string? StockFile { get; init; }

    /// <summary>A folder with a Cimatron Multi STL / Data Interface export (target/fixture/other files by name).</summary>
    public string? GeometryFolder { get; init; }

    /// <summary>The NC file to take when the post writes several.</summary>
    public string? NcFile { get; init; }

    /// <summary>Row-major 4×4 UCS→model matrix, when the API cannot read the UCS transformation.</summary>
    public IReadOnlyList<double>? UcsMatrix { get; init; }

    /// <summary>The operator asserts the geometry was exported in the procedure UCS (mode A).</summary>
    public bool GeometryInUcs { get; init; }

    public double MeshToleranceMm { get; init; } = 0.01;
    public bool MeshToleranceGiven { get; init; }
    public string? ReportPath { get; init; }
    public int KeepSuperseded { get; init; } = 5;

    internal const string Usage = """
        Meimad.CimatronExporter — writes a Meimad simulation package from the active Cimatron NC document.

          Meimad.CimatronExporter.exe --active-cimatron --output <folder> --post <post name> --ucs <UCS name> [options]
          Meimad.CimatronExporter.exe --probe
          Meimad.CimatronExporter.exe --register-cimatron

        Required:
          --active-cimatron          use the running Cimatron (the only source in this version)
          --output <folder>          the Operation export folder; the package goes to <folder>\cimatron-export
          --post <name>              Cimatron post-processor
          --ucs <name>               procedure UCS the NC is posted to and the geometry is placed in
        Operation and machine:
          --operation <number>       Operation number (e.g. 010); --operation-name <text>
          --toolpath <name>          the Cimatron toolpath to export (required when the document has several):
                                     only it is posted, and its setup's stock procedure gives the initial stock
          --procedure <id>           NC procedure id when the API cannot list procedures
          --part-number <text>       part number (Case); --part-name <text>
          --machine-id <id>          external machine id; --machine-name <text>; --axes <B_C|A_C|XYZ>
          --work-offset <G54..>      work offset the post uses (default G54)
          --start-home               post with start-home
        Stock and geometry:
          --stock initial|remaining|auto     default auto (remaining when --stock-procedure is given)
          --stock-procedure <id>     procedure whose remaining stock is exported (Cimatron 2026+)
          --source-operation <num>   Operation the remaining stock comes from
          --stock-file <stl>         take an existing stock STL (fallback rule; reported as a warning); without it
                                     a single STOCK*.stl beside the Cimatron document (or in its NC folder) is taken
          --geometry-folder <dir>    take a Multi STL export (files named *TARGET*/*FIXTURE*)
          --geometry-in-ucs          the geometry is already in the procedure UCS (mode A)
          --ucs-matrix "m00,...,m33" row-major UCS→model matrix (mode B) when the API cannot read it
          --mesh-tolerance <mm>      mesh tolerance used by the export (else the default is reported)
        Other:
          --nc-file <name>           the NC file to take when the post writes several
          --package-name <name>      package folder name (default cimatron-export)
          --report <path>            also write the human-readable report there
          (no argument) or --ui      open the one-click export window (toolbar button, desktop shortcut);
                                     presets: --toolpath, --post, --ucs, --operation, --case-folder,
                                     --case-name; --auto exports at once when every field is known
          --from-post                for the Cimatron post-processor hook: starts the window detached and
                                     returns at once; ignored while an export or the window is running
          --probe                    list the Cimatron API members this PC exposes and exit; without
                                     Cimatron automation, list what this PC has of it
          --progid <name>            the Cimatron automation ProgID (default CimAppAccess.AppAccess)
          --cimatron-folder <path>   Cimatron's Program folder, used when the ProgID is not registered
                                     (default: the newest installed Cimatron)
          --register-cimatron        register Cimatron's type libraries and exit (needed when Cimatron calls
                                     fail with "Library not registered"): for the current Windows user from a
                                     normal window, for every user from an administrator window;
                                     --unregister-cimatron removes them again

        Exit codes: 0 package written (maybe with warnings), 1 blocked, 2 usage error, 3 Cimatron not available.
        """;

    /// <summary>Parses the command line; returns null with an error message on a usage error.</summary>
    internal static (ExportOptions? Options, string? Error) Parse(IReadOnlyList<string> args)
    {
        var options = new ExportOptions();
        for (var index = 0; index < args.Count; index++)
        {
            var name = args[index];
            string Value()
            {
                if (index + 1 >= args.Count) throw new FormatException($"{name} needs a value.");
                return args[++index];
            }

            try
            {
                options = name.ToLowerInvariant() switch
                {
                    "--active-cimatron" => options with { ActiveCimatron = true },
                    "--probe" => options with { Probe = true },
                    "--register-cimatron" => options with { RegisterCimatron = true },
                    "--unregister-cimatron" => options with { UnregisterCimatron = true },
                    "--progid" => options with { ProgId = Value() },
                    "--cimatron-folder" => options with { CimatronFolder = Value() },
                    "--output" => options with { Output = Value() },
                    "--package-name" => options with { PackageFolderName = Value() },
                    "--post" => options with { Post = Value() },
                    "--ucs" => options with { Ucs = Value() },
                    "--work-offset" => options with { WorkOffset = Value() },
                    "--start-home" => options with { StartHome = true },
                    "--operation" => options with { OperationNumber = Value() },
                    "--operation-name" => options with { OperationName = Value() },
                    "--procedure" => options with { ProcedureId = Int(Value()) },
                    "--toolpath" => options with { Toolpath = Value() },
                    "--part-number" => options with { PartNumber = Value() },
                    "--part-name" => options with { PartName = Value() },
                    "--machine-id" => options with { MachineExternalId = Value() },
                    "--machine-name" => options with { MachineName = Value() },
                    "--axes" => options with { AxisConfiguration = Value() },
                    "--stock" => options with { Stock = StockMode(Value()) },
                    "--stock-procedure" => options with { StockProcedureId = Int(Value()) },
                    "--source-operation" => options with { SourceOperation = Value() },
                    "--stock-file" => options with { StockFile = Value() },
                    "--geometry-folder" => options with { GeometryFolder = Value() },
                    "--geometry-in-ucs" => options with { GeometryInUcs = true },
                    "--ucs-matrix" => options with { UcsMatrix = Matrix(Value()) },
                    "--mesh-tolerance" => options with { MeshToleranceMm = Double(Value()), MeshToleranceGiven = true },
                    "--nc-file" => options with { NcFile = Value() },
                    "--report" => options with { ReportPath = Value() },
                    _ => throw new FormatException($"Unknown option {name}.")
                };
            }
            catch (FormatException exception)
            {
                return (null, exception.Message);
            }
        }

        if (options.RegisterCimatron && options.UnregisterCimatron) return (null, "--register-cimatron and --unregister-cimatron exclude each other.");
        if (options.Probe || options.RegisterCimatron || options.UnregisterCimatron) return (options, null);
        if (!options.ActiveCimatron) return (null, "--active-cimatron is required (the exporter reads the running Cimatron).");
        if (string.IsNullOrWhiteSpace(options.Output) || !Path.IsPathFullyQualified(options.Output)) return (null, "--output must be a full folder path.");
        if (string.IsNullOrWhiteSpace(options.Post)) return (null, "--post is required.");
        if (string.IsNullOrWhiteSpace(options.Ucs)) return (null, "--ucs is required.");
        if (options.PackageFolderName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || options.PackageFolderName.StartsWith('.'))
            return (null, "--package-name must be a plain folder name.");
        return (options, null);
    }

    private static int Int(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number >= 0
            ? number
            : throw new FormatException($"'{value}' is not a whole number.");

    private static double Double(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) && number > 0
            ? number
            : throw new FormatException($"'{value}' is not a positive number.");

    private static string StockMode(string value) =>
        value.ToLowerInvariant() is "initial" or "remaining" or "auto" ? value.ToLowerInvariant() : throw new FormatException("--stock must be initial, remaining or auto.");

    private static IReadOnlyList<double> Matrix(string value)
    {
        var parts = value.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 16) throw new FormatException("--ucs-matrix needs 16 numbers (row-major 4×4).");
        return parts.Select(part => double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number
            : throw new FormatException($"'{part}' in --ucs-matrix is not a number.")).ToArray();
    }
}
