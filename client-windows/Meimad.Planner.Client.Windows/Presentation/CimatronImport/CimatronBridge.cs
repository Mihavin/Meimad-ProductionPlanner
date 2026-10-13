using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Meimad.Planner.Client.Windows.Presentation.CimatronImport;

/// <summary>The active Cimatron NC document as the exporter reads it (<c>--list-json</c>).</summary>
internal sealed record CimatronListing(
    bool Ok,
    CimatronBridgeError? Error,
    string? ExporterVersion,
    string? AdapterVersion,
    string? CimatronVersion,
    string? ConnectedBy,
    CimatronDocument? Document,
    IReadOnlyList<CimatronSetup>? Setups,
    IReadOnlyList<string>? UcsNames);

internal sealed record CimatronBridgeError(string Code, string Message);

internal sealed record CimatronDocument(string Name, string? Path, string? DocumentId, bool? Modified);

internal sealed record CimatronSetup(string Name, int? StockProcedureId, IReadOnlyList<CimatronToolpath>? Toolpaths);

internal sealed record CimatronToolpath(int Id, string Name, IReadOnlyList<CimatronProcedure>? Procedures);

internal sealed record CimatronProcedure(int Id, string Name, string? Ucs, bool Calculated);

/// <summary>The Meimad side of an import, written into the package manifest.</summary>
internal sealed record CimatronImportContextData(string ImportId, string CaseId, string CaseOperationId, string BasedOnVersion);

/// <summary>The export request the exporter reads (<c>--export-json</c>).</summary>
internal sealed record CimatronExportRequest(
    string Output,
    string PackageFolderName,
    string Post,
    string Ucs,
    IReadOnlyList<int> ToolpathIds,
    IReadOnlyList<int> ProcedureIds,
    string Operation,
    string? OperationName,
    string? PartNumber,
    string? GeometryFolder,
    string? ExpectedDocumentPath,
    bool RequireSavedDocument,
    CimatronImportContextData Context);

internal sealed record CimatronExportResult(
    bool Ok,
    CimatronBridgeError? Error,
    bool Complete,
    string? PackagePath,
    int ExitCode,
    string? Report,
    IReadOnlyList<CimatronBridgeEntry>? Entries);

internal sealed record CimatronBridgeEntry(string Status, string Step, string? Code, string Message);

/// <summary>The Cimatron exporter installed with the client, run as a helper process.</summary>
internal interface ICimatronBridge
{
    Task<CimatronListing> ListAsync(CancellationToken cancellationToken);

    Task<CimatronExportResult> ExportAsync(CimatronExportRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Runs <c>cimatron-exporter\Meimad.CimatronExporter.exe</c> beside the client (installed, updated and removed with it) with
/// <c>--list-json</c> or <c>--export-json</c>; the answer is a UTF-8 JSON file. Cancelling kills the exporter; the package
/// it was writing stays in its staging folder and is never published. <c>MEIMAD_CIMATRON_EXPORTER</c> names another exe
/// (development).
/// </summary>
internal sealed class CimatronBridgeProcess : ICimatronBridge
{
    internal const string ExporterFolderName = "cimatron-exporter";
    internal const string ExporterFileName = "Meimad.CimatronExporter.exe";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    internal static string? ExecutablePath()
    {
        var configured = Environment.GetEnvironmentVariable("MEIMAD_CIMATRON_EXPORTER");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        var installed = Path.Combine(AppContext.BaseDirectory, ExporterFolderName, ExporterFileName);
        return File.Exists(installed) ? installed : null;
    }

    public async Task<CimatronListing> ListAsync(CancellationToken cancellationToken)
    {
        if (ExecutablePath() is null) return new CimatronListing(false, NotInstalled(), null, null, null, null, null, null, null);
        var (answer, error) = await RunAsync<CimatronListing>(["--list-json"], TimeSpan.FromMinutes(2), cancellationToken);
        return answer ?? new CimatronListing(false, error, null, null, null, null, null, null, null);
    }

    public async Task<CimatronExportResult> ExportAsync(CimatronExportRequest request, CancellationToken cancellationToken)
    {
        if (ExecutablePath() is null) return new CimatronExportResult(false, NotInstalled(), false, null, 3, null, null);
        var folder = WorkFolder();
        var requestFile = Path.Combine(folder, "request.json");
        await File.WriteAllTextAsync(requestFile, JsonSerializer.Serialize(request, Json), cancellationToken);
        try
        {
            var (answer, error) = await RunAsync<CimatronExportResult>(["--export-json", requestFile], TimeSpan.FromMinutes(20), cancellationToken);
            return answer ?? new CimatronExportResult(false, error, false, null, 3, error?.Message, null);
        }
        finally
        {
            TryDelete(folder);
        }
    }

    private static CimatronBridgeError NotInstalled() => new("exporter_not_installed",
        "The Cimatron exporter is not installed with this Meimad client; install the current client version. Import from Cimatron also needs Cimatron on this PC.");

    private static async Task<(T? Answer, CimatronBridgeError? Error)> RunAsync<T>(IReadOnlyList<string> arguments, TimeSpan timeout,
        CancellationToken cancellationToken) where T : class
    {
        var folder = WorkFolder();
        var result = Path.Combine(folder, "result.json");
        var info = new ProcessStartInfo(ExecutablePath()!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = folder
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        info.ArgumentList.Add("--result");
        info.ArgumentList.Add(result);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            using var process = Process.Start(info) ?? throw new InvalidOperationException("The Cimatron exporter did not start.");
            try
            {
                await process.WaitForExitAsync(limit.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Already ended.
                }

                if (cancellationToken.IsCancellationRequested) throw;
                return (null, new CimatronBridgeError("exporter_timeout", $"The Cimatron exporter did not answer within {timeout.TotalMinutes:0} minutes."));
            }

            if (!File.Exists(result))
                return (null, new CimatronBridgeError("exporter_failed", $"The Cimatron exporter ended (exit code {process.ExitCode}) without an answer."));
            return (JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(result, CancellationToken.None), Json), null);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException or JsonException
                                              or UnauthorizedAccessException or InvalidOperationException)
        {
            return (null, new CimatronBridgeError("exporter_failed", exception.Message));
        }
        finally
        {
            TryDelete(folder);
        }
    }

    private static string WorkFolder() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "MeimadPlanner", "cimatron", Guid.NewGuid().ToString("N"))).FullName;

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A temporary folder left behind is harmless.
        }
    }
}
