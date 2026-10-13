using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Meimad.Planner.SimulationPackage.Manifest;

namespace Meimad.CimatronExporter;

/// <summary>What the Meimad client's import window shows of the active Cimatron NC document (<c>--list-json</c>).</summary>
internal sealed record BridgeListing(
    bool Ok,
    BridgeError? Error,
    string ExporterVersion,
    string? AdapterVersion,
    string? CimatronVersion,
    string? ConnectedBy,
    BridgeDocument? Document,
    IReadOnlyList<BridgeSetup> Setups,
    IReadOnlyList<string> UcsNames);

internal sealed record BridgeError(string Code, string Message);

internal sealed record BridgeDocument(string Name, string? Path, string? DocumentId, bool? Modified);

internal sealed record BridgeSetup(string Name, int? StockProcedureId, IReadOnlyList<BridgeToolpath> Toolpaths);

internal sealed record BridgeToolpath(int Id, string Name, IReadOnlyList<BridgeProcedure> Procedures);

internal sealed record BridgeProcedure(int Id, string Name, string? Ucs, bool Calculated);

/// <summary>The client's export request (<c>--export-json &lt;file&gt;</c>).</summary>
internal sealed record BridgeExportRequest
{
    /// <summary>The folder the package folder is written into (the managed revision folder on the share).</summary>
    public string Output { get; init; } = "";
    public string PackageFolderName { get; init; } = "cimatron-export";
    public string Post { get; init; } = "";
    public string Ucs { get; init; } = "MODEL";
    public IReadOnlyList<int>? ToolpathIds { get; init; }
    public IReadOnlyList<int>? ProcedureIds { get; init; }
    public string Operation { get; init; } = "";
    public string? OperationName { get; init; }
    public string? PartNumber { get; init; }
    public string? PartName { get; init; }
    public string? MachineExternalId { get; init; }
    public string? MachineName { get; init; }
    public string? GeometryFolder { get; init; }
    public string? ExpectedDocumentPath { get; init; }
    public bool RequireSavedDocument { get; init; } = true;
    public MeimadImportContext? Context { get; init; }
}

internal sealed record BridgeExportResult(
    bool Ok,
    BridgeError? Error,
    bool Complete,
    string? PackagePath,
    int ExitCode,
    string Report,
    IReadOnlyList<BridgeEntry> Entries);

internal sealed record BridgeEntry(string Status, string Step, string? Code, string Message);

/// <summary>
/// The exporter as the Meimad client's helper process: <c>--list-json</c> reads the active NC document, <c>--export-json
/// &lt;request&gt;</c> exports the chosen CAM operations; both write their JSON answer (UTF-8, camelCase) to <c>--result
/// &lt;file&gt;</c> so Hebrew paths survive. Exit codes as the console: 0 done, 1 blocked, 2 usage error, 3 Cimatron not available.
/// </summary>
internal static class ClientBridge
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static bool Handles(IReadOnlyList<string> args) =>
        args.Any(arg => arg.Equals("--list-json", StringComparison.OrdinalIgnoreCase) || arg.Equals("--export-json", StringComparison.OrdinalIgnoreCase));

    public static int Run(IReadOnlyList<string> args, Func<ICimatronApi> connect)
    {
        string? Value(string name)
        {
            for (var index = 0; index + 1 < args.Count; index++)
                if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[index + 1];
            return null;
        }

        var resultPath = Value("--result");
        if (string.IsNullOrWhiteSpace(resultPath))
        {
            Console.Error.WriteLine("--result <file> is required with --list-json and --export-json.");
            return 2;
        }

        if (args.Any(arg => arg.Equals("--list-json", StringComparison.OrdinalIgnoreCase)))
        {
            var listing = List(connect);
            Write(resultPath, listing);
            return listing.Ok ? 0 : 3;
        }

        var requestPath = Value("--export-json");
        BridgeExportRequest? request;
        try
        {
            request = requestPath is null ? null : JsonSerializer.Deserialize<BridgeExportRequest>(File.ReadAllText(requestPath), Json);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            Write(resultPath, Failed("request_unreadable", exception.Message, 2));
            return 2;
        }

        if (request is null)
        {
            Write(resultPath, Failed("request_unreadable", "--export-json needs the request file.", 2));
            return 2;
        }

        var result = Export(request, connect, TimeProvider.System);
        Write(resultPath, result);
        return result.ExitCode;
    }

    public static BridgeListing List(Func<ICimatronApi> connect)
    {
        var version = PackageExporterVersion();
        try
        {
            using var api = connect();
            var document = api.GetActiveNcDocument();
            var procedures = api.GetProcedures();
            var toolpaths = api.GetToolpaths();
            var setups = toolpaths
                .GroupBy(item => item.SetupName ?? "", StringComparer.OrdinalIgnoreCase)
                .Select(group => new BridgeSetup(
                    group.Key,
                    group.Select(item => item.StockProcedureId).FirstOrDefault(id => id is not null),
                    group.Select(item => new BridgeToolpath(item.Id, item.Name,
                        procedures.Where(procedure => string.Equals(procedure.OperationName, item.Name, StringComparison.OrdinalIgnoreCase))
                            .Select(procedure => new BridgeProcedure(procedure.Id, procedure.Name, procedure.UcsName, procedure.HasToolpath))
                            .ToArray())).ToArray()))
                .ToArray();
            return new BridgeListing(true, null, version, api.AdapterVersion, api.CimatronVersion, CimatronComApi.ConnectedBy,
                new BridgeDocument(document.Name, document.FilePath, document.DocumentId, document.Modified), setups, api.GetUcsNames());
        }
        catch (CimatronApiException exception)
        {
            return new BridgeListing(false, new BridgeError(exception.Code, exception.Message), version, null, null, null, null, [], []);
        }
    }

    public static BridgeExportResult Export(BridgeExportRequest request, Func<ICimatronApi> connect, TimeProvider clock)
    {
        if (string.IsNullOrWhiteSpace(request.Output) || !Path.IsPathFullyQualified(request.Output))
            return Failed("request_invalid", "The output folder must be a full path.", 2);
        if (string.IsNullOrWhiteSpace(request.Post)) return Failed("request_invalid", "The postprocessor is required.", 2);
        if (string.IsNullOrWhiteSpace(request.Operation)) return Failed("request_invalid", "The Operation number is required.", 2);
        if (request.ToolpathIds is not { Count: > 0 } && request.ProcedureIds is not { Count: > 0 })
            return Failed("request_invalid", "Choose at least one CAM operation.", 2);

        var options = new ExportOptions
        {
            ActiveCimatron = true,
            Output = request.Output,
            PackageFolderName = request.PackageFolderName,
            Post = request.Post.Trim(),
            Ucs = string.IsNullOrWhiteSpace(request.Ucs) ? "MODEL" : request.Ucs.Trim(),
            OperationNumber = request.Operation.Trim(),
            OperationName = request.OperationName,
            PartNumber = request.PartNumber,
            PartName = request.PartName,
            MachineExternalId = request.MachineExternalId,
            MachineName = request.MachineName,
            GeometryFolder = string.IsNullOrWhiteSpace(request.GeometryFolder) ? null : request.GeometryFolder,
            ToolpathIds = request.ToolpathIds,
            ProcedureIds = request.ProcedureIds,
            ExpectedDocumentPath = request.ExpectedDocumentPath,
            RequireSavedDocument = request.RequireSavedDocument,
            ImportContext = request.Context
        };

        try
        {
            using var api = connect();
            // Held while exporting: the exporter's own post must not start another export through a post-processor hook.
            using var held = ExporterLocks.TryTake(ExporterLocks.Export);
            if (held is null) return Failed("export_running", "Another Meimad export is running on this PC; wait for it to finish.", 1);
            var result = new PackageExporter(api, clock).Export(options);
            return new BridgeExportResult(true, null, result.Complete, result.PackagePath, result.ExitCode, result.Log.ToReport(),
                result.Log.Entries.Select(entry => new BridgeEntry(entry.Status, entry.Step, entry.Code, entry.Message)).ToArray());
        }
        catch (CimatronApiException exception)
        {
            return Failed(exception.Code, exception.Message, 3);
        }
    }

    private static BridgeExportResult Failed(string code, string message, int exitCode) =>
        new(false, new BridgeError(code, message), false, null, exitCode, $"{code}: {message}", []);

    private static void Write<T>(string path, T value)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path));
        if (folder is not null) Directory.CreateDirectory(folder);
        File.WriteAllText(path, JsonSerializer.Serialize(value, Json), new System.Text.UTF8Encoding(false));
    }

    private static string PackageExporterVersion() =>
        typeof(ClientBridge).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
