using System.ComponentModel;
using System.Collections.ObjectModel;
using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Presentation.NcViewer;
using Meimad.Planner.NcEngine;
using System.Security.Cryptography;

namespace Meimad.Planner.Client.Windows.Presentation;

internal sealed class OperationWorkbenchViewModel : INotifyPropertyChanged
{
    private IPlannerApiClient? api;
    private int generation;
    private bool busy;
    private bool canRecordHandoff;
    private bool updatingContexts;
    private string clientId = "", userId = "";
    private readonly Dictionary<string, (string Id, ProductionPackageContext Context)> pendingBuilds = [];
    public WorkbenchContext? Context { get; private set; }
    public ObservableCollection<ProductionPackageContext> Contexts { get; } = [];
    private ProductionPackageContext? selectedContext;
    public ProductionPackageContext? SelectedContext
    {
        get => selectedContext;
        set
        {
            if (updatingContexts || selectedContext == value) return;
            generation++; busy = false;
            selectedContext = value;
            Handoff.AttachSession(api, canRecordHandoff);
            _ = Ai.CancelAsync();
            if (Context is not null && value is not null) Context = Context with { ExactContext = value, MachineId = value.MachineId };
            Package = null; Changed();
        }
    }
    private ProductionPackageInfo? package;
    public ProductionPackageInfo? Package
    {
        get => package;
        private set { package = value; IntegrityStatus = "Files have not been verified in this view."; }
    }
    public OperationWorkbenchDetails? Details { get; private set; }
    public bool IsHistorical { get; private set; }
    public AsyncCommand PreviousPackageCommand { get; }
    public AsyncCommand ExportPackageCommand { get; }
    public AsyncCommand BuildMeasuredCommand { get; }
    public AsyncCommand BuildManualCommand { get; }
    public AsyncCommand VerifyFilesCommand { get; }
    public AsyncCommand InspectNcCommand { get; }
    public string IntegrityStatus { get; private set; } = "Files have not been verified in this view.";
    internal event EventHandler<NcViewerOpenRequest>? NcRequested;
    internal event EventHandler<ProductionPackageInfo>? ExportRequested;
    public PlannerProductionReadiness? Readiness { get; private set; }
    public string Status { get; private set; } = "Open an operation from Shop or Preparation.";
    public AsyncCommand RefreshCommand { get; }
    public LocalAiPanelViewModel Ai { get; } = new();
    public PackageHandoffViewModel Handoff { get; } = new();
    public AsyncCommand ExplainBlockersCommand { get; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public OperationWorkbenchViewModel()
    {
        RefreshCommand = new(RefreshAsync, () => api is not null && Context is not null && !busy);
        ExplainBlockersCommand = new(() => Ai.ExplainAsync(selectedContext!.ProductionRunId), () => api is not null && selectedContext is not null && !busy);
        PreviousPackageCommand = new(OpenPreviousPackageAsync, () => api is not null && !busy && Package?.SupersedesProductionPackageId is not null);
        ExportPackageCommand = new(() => { if (Package is { } package) ExportRequested?.Invoke(this, package); return Task.CompletedTask; },
            () => api is not null && !busy && !IsHistorical && Package?.FileExportAvailable == true);
        BuildMeasuredCommand = new(() => BuildAsync("MEASURED"), () => api is not null && selectedContext is not null && !busy && !IsHistorical);
        BuildManualCommand = new(() => BuildAsync("MANUAL_DUMMY"), () => api is not null && selectedContext is not null && !busy && !IsHistorical);
        VerifyFilesCommand = new(() => InspectFilesAsync(false), () => api is not null && Package is not null && !busy);
        InspectNcCommand = new(() => InspectFilesAsync(true), () => api is not null && Package?.Artifacts.Any(x => x.ArtifactType == "RUNNABLE_NC") == true && !busy);
    }
    internal void AttachSession(IPlannerApiClient? client, bool prepareTools = false, string clientIdentity = "", string userIdentity = "")
    {
        clientId = clientIdentity; userId = userIdentity; pendingBuilds.Clear(); IsHistorical = false;
        generation++; api = client; Context = null; Package = null; Details = null; Readiness = null; busy = false; Contexts.Clear(); selectedContext = null;
        Status = "Open an operation from Shop or Preparation."; Changed();
        Ai.AttachSession(client);
        canRecordHandoff = prepareTools; Handoff.AttachSession(client, prepareTools);
    }
    internal async Task OpenAsync(WorkbenchContext context)
    {
        var opening = ++generation;
        await Ai.CancelAsync();
        if (opening != generation) return;
        Handoff.AttachSession(api, canRecordHandoff);
        generation++; Context = context; Package = null; Details = null; Readiness = null; busy = false; Contexts.Clear(); selectedContext = null;
        Changed(); await RefreshAsync();
    }
    internal async Task BuildAsync(string mode)
    {
        if (api is not { } client || selectedContext is not { } exact || busy || IsHistorical) return;
        var key = $"{exact.MachineAssignmentId}/{exact.ProductionRunProgramId}/{exact.ProductionRunOutputId}/{mode}";
        if (!pendingBuilds.TryGetValue(key, out var pending))
            pendingBuilds[key] = pending = (Guid.NewGuid().ToString("N"), exact);
        var current = generation; busy = true; Changed();
        try
        {
            var built = await client.CreateProductionPackageAsync(exact.BatchOperationId, clientId, userId, mode,
                context: pending.Context, requestId: pending.Id);
            if (pendingBuilds.TryGetValue(key, out var recorded) && recorded.Id == pending.Id) pendingBuilds.Remove(key);
            if (current != generation) return;
            Package = built; IsHistorical = false;
            await Handoff.OpenAsync(built.ProductionPackageId, exact);
            if (current == generation) Status = $"Built package #{built.PackageNumber}. Refresh before export to review current validity. No physical handoff or setup start was recorded.";
        }
        catch (PlannerApiException exception) when ((int)exception.StatusCode is >= 400 and < 500 && (int)exception.StatusCode is not (408 or 429))
        {
            if (pendingBuilds.TryGetValue(key, out var recorded) && recorded.Id == pending.Id) pendingBuilds.Remove(key);
            if (current == generation) Status = "Build refused; the selected context is retained. Refresh and review: " + exception.Message;
        }
        catch (Exception exception)
        { if (current == generation) Status = "Build outcome uncertain; retry this context and offset mode to reuse the same request identity. " + exception.Message; }
        finally { if (current == generation) { busy = false; Changed(); } }
    }
    internal async Task InspectFilesAsync(bool openNc)
    {
        if (api is not { } client || Package is not { } package || busy) return;
        var current = generation; busy = true; IntegrityStatus = "Verifying immutable artifact bytes..."; Changed();
        try
        {
            var artifacts = openNc ? package.Artifacts.Where(x => x.ArtifactType == "RUNNABLE_NC").ToArray() : package.Artifacts;
            foreach (var artifact in artifacts)
            {
                var bytes = await client.ReadHistoricalPackageArtifactAsync(package.ProductionPackageId, artifact.ArtifactId);
                if (current != generation) return;
                if (bytes.LongLength != artifact.FileSize || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Artifact checksum/length mismatch: " + artifact.LogicalPath);
                if (openNc) NcRequested?.Invoke(this, new($"Package #{package.PackageNumber} / {package.ProductionPackageId}", artifact.LogicalPath,
                    NcTextFile.Decode(bytes), true, $"Immutable runnable artifact; {(IsHistorical ? "HISTORICAL" : "sampled current")}; SHA-256 {artifact.Sha256}. No file is sent to a CNC."));
            }
            IntegrityStatus = $"Verified {artifacts.Count} artifact(s) at {DateTimeOffset.UtcNow:u}. Hashes match this immutable package; current readiness remains a separate check.";
        }
        catch (Exception exception) { if (current == generation) IntegrityStatus = "Verification failed: " + exception.Message; }
        finally { if (current == generation) { busy = false; Changed(); } }
    }
    internal async Task RefreshAsync()
    {
        if (busy || api is not { } client || Context is not { } context) return;
        Handoff.AttachSession(api, canRecordHandoff);
        var current = generation; busy = true; IsHistorical = false; Package = null; Details = null; Readiness = null; Changed();
        try
        {
            // The broad operation readiness is explicitly labelled; exact package resolution
            // requires all Run/program/output IDs when an operation is split.
            var readiness = await client.GetProductionReadinessAsync(context.BatchOperationId);
            if (current != generation) return;
            var contexts = await client.GetExecutionContextsAsync(context.BatchOperationId);
            if (current != generation) return;
            var exact = context.ExactContext is { } original
                ? contexts.FirstOrDefault(x => x.MachineAssignmentId == original.MachineAssignmentId && x.ProductionRunProgramId == original.ProductionRunProgramId && x.ProductionRunOutputId == original.ProductionRunOutputId)
                : contexts.Count == 1 ? contexts[0] : null;
            Readiness = readiness;
            updatingContexts = true;
            Contexts.Clear(); foreach (var candidate in contexts) Contexts.Add(candidate);
            selectedContext = exact;
            if (exact is not null) Context = context with { ExactContext = exact, MachineId = exact.MachineId };
            PropertyChanged?.Invoke(this, new(nameof(SelectedContext)));
            updatingContexts = false;
            var details = await client.GetOperationWorkbenchAsync(context.BatchOperationId, exact);
            if (current != generation) return;
            Details = details;
            Context = Context! with { CaseId = details.CaseId, BatchId = details.BatchId };
            if (exact is null)
            {
                Status = contexts.Count == 0 ? "No active execution context. Review the Machine assignment and Run."
                    : "Select the exact Run/program/output, then refresh. No split context is chosen automatically.";
                return;
            }
            var package = await client.GetCurrentProductionPackageAsync(context.BatchOperationId, context: exact);
            if (current != generation) return;
            Package = package;
            if (package is not null) await Handoff.OpenAsync(package.ProductionPackageId, exact);
            if (current != generation) return;
            Status = package is null ? "No valid current package for this context. Review preparation facts."
                : $"Current package {package.PackageNumber}, sampled {DateTimeOffset.UtcNow:u}. Physical handoff and loader execution are separate facts.";
        }
        catch (Exception exception)
        { if (current == generation) Status = $"Refresh/review required: {exception.Message}"; }
        finally { if (current == generation) { busy = false; Changed(); } }
    }
    internal async Task OpenPreviousPackageAsync()
    {
        if (api is not { } client || busy || Package?.SupersedesProductionPackageId is not { } id) return;
        var current = ++generation; busy = true; Handoff.AttachSession(null, false); Changed();
        try
        {
            var historical = await client.GetHistoricalPackageAsync(id);
            if (current != generation) return;
            Package = historical; IsHistorical = true;
            Status = $"HISTORICAL package {historical.PackageNumber}. Inspection only; this is not the current package. Refresh context to return to current evidence.";
        }
        catch (Exception exception) { if (current == generation) Status = exception.Message; }
        finally { if (current == generation) { busy = false; Changed(); } }
    }
    private void Changed()
    {
        PropertyChanged?.Invoke(this, new(null)); RefreshCommand.RaiseCanExecuteChanged(); ExplainBlockersCommand.RaiseCanExecuteChanged();
        PreviousPackageCommand.RaiseCanExecuteChanged(); ExportPackageCommand.RaiseCanExecuteChanged();
        BuildMeasuredCommand.RaiseCanExecuteChanged(); BuildManualCommand.RaiseCanExecuteChanged();
        VerifyFilesCommand.RaiseCanExecuteChanged(); InspectNcCommand.RaiseCanExecuteChanged();
    }
}
