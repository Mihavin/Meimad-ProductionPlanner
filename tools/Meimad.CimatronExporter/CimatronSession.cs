using System.Collections.Concurrent;

namespace Meimad.CimatronExporter;

/// <summary>What the window shows of the active Cimatron NC document.</summary>
internal sealed record CimatronSnapshot(
    CimatronDocumentInfo Document,
    IReadOnlyList<CimatronToolpath> Toolpaths,
    IReadOnlyList<string> UcsNames,
    string? CimatronVersion);

/// <summary>
/// Cimatron's objects for the exporter window, on their own single-threaded apartment: every call into Cimatron runs on
/// that thread, so the window stays responsive while Cimatron is busy (a post running) or an export takes its 20 seconds.
/// </summary>
internal sealed class CimatronSession : IDisposable
{
    private readonly BlockingCollection<Action> work = new();
    private readonly Thread thread;
    private CimatronComApi? api;

    public CimatronSession()
    {
        thread = new Thread(() =>
        {
            foreach (var item in work.GetConsumingEnumerable()) item();
            api?.Dispose();
        })
        {
            IsBackground = true,
            Name = "Cimatron"
        };
        if (OperatingSystem.IsWindows()) thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    /// <summary>
    /// Connects (again) to the running Cimatron and reads the active NC document. Retries until <paramref name="wait"/> has
    /// passed, for a start from the post-processor while Cimatron is still posting.
    /// </summary>
    public Task<CimatronSnapshot> ReadAsync(TimeSpan wait, string? progId) => Run(() =>
    {
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            try
            {
                api?.Dispose();
                api = null;
                api = CimatronComApi.Connect(progId);
                var document = api.GetActiveNcDocument();
                return new CimatronSnapshot(document, api.GetToolpaths(), api.GetUcsNames(), api.CimatronVersion);
            }
            catch (CimatronApiException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(2000);
            }
        }
    });

    /// <summary>Exports one package, holding <see cref="ExporterLocks.Export"/> so the exporter's own post does not start another export.</summary>
    public Task<ExportResult> ExportAsync(ExportOptions options) => Run(() =>
    {
        if (api is null) throw new CimatronApiException("cimatron_not_connected", "Cimatron is not connected; press Refresh.");
        using var held = ExporterLocks.TryTake(ExporterLocks.Export)
            ?? throw new CimatronApiException("export_running", "Another Meimad export is running; wait for it to finish.");
        return new PackageExporter(api, TimeProvider.System).Export(options);
    });

    private Task<T> Run<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        work.Add(() =>
        {
            try
            {
                completion.SetResult(action());
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        });
        return completion.Task;
    }

    public void Dispose()
    {
        work.CompleteAdding();
        thread.Join(TimeSpan.FromSeconds(10));
        work.Dispose();
    }
}
