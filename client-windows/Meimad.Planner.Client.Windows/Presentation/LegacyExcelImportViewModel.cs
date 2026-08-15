using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using Meimad.Planner.Client.Windows.Api;

namespace Meimad.Planner.Client.Windows.Presentation;

/// <summary>
/// Stages a legacy workbook for an explicit, server-validated import. This
/// type deliberately does not interpret workbook values or derive routes,
/// quantities, dates, or Machine assignments.
/// </summary>
internal sealed class LegacyExcelImportViewModel : INotifyPropertyChanged
{
    private IPlannerApiClient? apiClient;
    private readonly Func<string, Stream> openWorkbook;
    private readonly Func<string, bool> workbookExists;
    private readonly DispatcherTimer expiryTimer;
    private string clientId = string.Empty;
    private long editGeneration;
    private bool isEditor;
    private bool isBusy;
    private string selectedFilePath = string.Empty;
    private string sourceSheetName = string.Empty;
    private string openOrdersSheetName = string.Empty;
    private string errorMessage = string.Empty;
    private string summary = "Choose an .xlsx workbook to create a Server preview.";
    private int headerRowNumber;
    private LegacyWorkingPlanPreview? preview;

    internal LegacyExcelImportViewModel(
        Func<string, Stream>? openWorkbook = null,
        Func<string, bool>? workbookExists = null)
    {
        this.openWorkbook = openWorkbook ?? (path => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));
        this.workbookExists = workbookExists ?? File.Exists;
        expiryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        expiryTimer.Tick += (_, _) =>
        {
            OnPropertyChanged(nameof(TokenExpiryText));
            if (preview is not null && preview.ExpiresAt <= DateTimeOffset.UtcNow) RaiseState();
        };
        PreviewCommand = new AsyncCommand(PreviewAsync, CanPreview);
        CommitCommand = new AsyncCommand(CommitAsync, () => CanCommit);
        SkipRowCommand = new AsyncCommand<LegacyImportRowViewModel>(SkipRowAsync, CanSkipRow);
        SkipAllUnresolvedCommand = new AsyncCommand(SkipAllUnresolvedAsync,
            () => IsEditor && !IsBusy && preview is not null);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised only after the Server accepts (or idempotently replays) a commit.</summary>
    public event EventHandler<LegacyWorkingPlanCommitReceipt>? ImportCommitted;

    public ObservableCollection<LegacyImportMappingViewModel> Mappings { get; } = [];

    public ObservableCollection<LegacyImportMappingViewModel> MachineMappings { get; } = [];

    public ObservableCollection<LegacyImportRowViewModel> Rows { get; } = [];

    public ObservableCollection<LegacyImportIssue> Issues { get; } = [];

    public AsyncCommand PreviewCommand { get; }

    public AsyncCommand CommitCommand { get; }

    public AsyncCommand<LegacyImportRowViewModel> SkipRowCommand { get; }

    public AsyncCommand SkipAllUnresolvedCommand { get; }

    public string SelectedFilePath
    {
        get => selectedFilePath;
        set
        {
            if (SetField(ref selectedFilePath, value))
            {
                ClearPreview();
                RaiseState();
            }
        }
    }

    public string SourceSheetName
    {
        get => sourceSheetName;
        set
        {
            if (SetField(ref sourceSheetName, value))
            {
                RaiseState();
            }
        }
    }

    public string OpenOrdersSheetName
    {
        get => openOrdersSheetName;
        set
        {
            if (SetField(ref openOrdersSheetName, value))
            {
                RaiseState();
            }
        }
    }

    public string ErrorMessage { get => errorMessage; private set => SetField(ref errorMessage, value); }

    public string Summary { get => summary; private set => SetField(ref summary, value); }

    public int HeaderRowNumber { get => headerRowNumber; private set => SetField(ref headerRowNumber, value); }

    public DateTimeOffset? ExpiresAt => preview?.ExpiresAt;

    public string TokenExpiryText => preview is null
        ? string.Empty
        : preview.ExpiresAt <= DateTimeOffset.UtcNow
            ? "Preview token has expired. Create a new preview before committing."
            : $"Preview token expires in {FormatRemaining(preview.ExpiresAt - DateTimeOffset.UtcNow)} "
              + $"({preview.ExpiresAt.ToLocalTime():HH:mm:ss}).";

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (SetField(ref isBusy, value))
            {
                RaiseState();
            }
        }
    }

    public bool IsEditor
    {
        get => isEditor;
        private set
        {
            if (SetField(ref isEditor, value))
            {
                RaiseState();
            }
        }
    }

    public bool CanCommit => apiClient is not null
        && IsEditor
        && !IsBusy
        && preview is not null
        && preview.ExpiresAt > DateTimeOffset.UtcNow
        && !HasGlobalServerBlockers()
        && Mappings.All(mapping => mapping.IsResolved)
        && HasUniqueColumnTargetFields()
        && MachineMappings.Where(mapping => Rows.Any(row => row.Kind == "planning"
            && row.SectionKey == mapping.SectionKey && !row.IsSkipped)).All(mapping => mapping.IsResolved)
        && Rows.All(row => row.HasExplicitDecision && row.IsResolved);

    public void SetWorkbookSelection(string path)
    {
        SelectedFilePath = path?.Trim() ?? string.Empty;
        if (!string.IsNullOrEmpty(SelectedFilePath)
            && !SelectedFilePath.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            ErrorMessage = "Choose an Excel .xlsx workbook.";
        }
        else
        {
            ErrorMessage = string.Empty;
        }
    }

    internal void AttachSession(IPlannerApiClient? newApiClient, string newClientId, EditModeStatus? editStatus)
    {
        if (!ReferenceEquals(apiClient, newApiClient))
        {
            apiClient = newApiClient;
            ClearPreview();
        }

        clientId = newClientId;
        editGeneration = editStatus?.Generation ?? 0;
        IsEditor = editStatus?.State == ClientEditState.Editor;
        RaiseState();
    }

    internal async Task PreviewAsync()
    {
        if (!CanPreview())
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            await using var workbook = openWorkbook(SelectedFilePath);
            var result = await apiClient!.PreviewLegacyWorkingPlanAsync(
                workbook,
                Path.GetFileName(SelectedFilePath));
            ApplyPreview(result);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            ErrorMessage = FriendlyMessage(exception);
            Summary = "The workbook was not previewed. Correct the reported issue and try again.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    internal async Task CommitAsync()
    {
        if (!CanCommit)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            var receipt = await apiClient!.CommitLegacyWorkingPlanAsync(
                BuildCommit(), clientId, editGeneration);
            Summary = receipt.Replayed
                ? $"Import {receipt.CommitId} was already committed; the Server replayed its receipt."
                : $"Import {receipt.CommitId} committed: {receipt.Created.CaseIds.Count} Cases, "
                  + $"{receipt.Created.OrderIds.Count} Orders, {receipt.Created.BatchIds.Count} Batches, "
                  + $"and {receipt.Created.AssignmentIds.Count} assignments.";
            ImportCommitted?.Invoke(this, receipt);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            ErrorMessage = FriendlyMessage(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private Task SkipRowAsync(LegacyImportRowViewModel? row)
    {
        if (row is not null)
        {
            row.IsSkipped = true;
        }

        return Task.CompletedTask;
    }

    internal Task SkipAllUnresolvedAsync()
    {
        if (!IsEditor || IsBusy)
        {
            return Task.CompletedTask;
        }

        foreach (var row in Rows.Where(row => !row.HasExplicitDecision))
        {
            row.IsSkipped = true;
        }

        return Task.CompletedTask;
    }

    private void ApplyPreview(LegacyWorkingPlanPreview result)
    {
        preview = result;
        expiryTimer.Start();
        SourceSheetName = result.Suggestions.PlanningSheet ?? string.Empty;
        OpenOrdersSheetName = result.Suggestions.OpenOrdersSheet ?? string.Empty;
        HeaderRowNumber = result.MachineSections.Count == 0
            ? 0
            : result.MachineSections.Min(section => section.HeaderRow);

        Mappings.Clear();
        MachineMappings.Clear();
        var planningColumnChoices = ColumnChoicesFor(result, result.Suggestions.PlanningSheet);
        var openOrderColumnChoices = ColumnChoicesFor(result, result.Suggestions.OpenOrdersSheet);
        foreach (var suggestion in result.Suggestions.PlanningColumns ?? [])
        {
            Mappings.Add(LegacyImportMappingViewModel.Column(
                "planning", suggestion, this, SampleFor(result.Rows, suggestion.Field), planningColumnChoices));
        }
        foreach (var suggestion in result.Suggestions.OpenOrderColumns ?? [])
        {
            Mappings.Add(LegacyImportMappingViewModel.Column(
                "open_orders", suggestion, this, SampleFor(result.OpenOrderRows, suggestion.Field), openOrderColumnChoices));
        }
        foreach (var section in result.MachineSections ?? [])
        {
            MachineMappings.Add(LegacyImportMappingViewModel.Machine(section, this));
        }

        Issues.Clear();
        foreach (var issue in result.Issues ?? [])
        {
            Issues.Add(issue);
        }

        Rows.Clear();
        foreach (var row in result.Rows ?? [])
        {
            Rows.Add(LegacyImportRowViewModel.Planning(row, IssuesFor(row.SheetName, row.RowNumber), this));
        }
        foreach (var row in result.OpenOrderRows ?? [])
        {
            Rows.Add(LegacyImportRowViewModel.OpenOrder(row, IssuesFor(row.SheetName, row.RowNumber), this));
        }

        var blockers = Issues.Count(issue => IsBlockingSeverity(issue.Severity));
        var warnings = Issues.Count(issue => string.Equals(issue.Severity, "warning", StringComparison.OrdinalIgnoreCase));
        Summary = $"Server preview: {Rows.Count} rows, {Mappings.Count} column mappings, {MachineMappings.Count} Machine mappings, {warnings} warnings, {blockers} blockers. "
            + "Choose every mapping and row decision before committing.";
        RaiseState();
    }

    private LegacyWorkingPlanCommit BuildCommit() => new(
        preview!.SchemaVersion,
        preview.ImportToken,
        preview.WorkbookSha256,
        NullIfBlank(SourceSheetName),
        NullIfBlank(OpenOrdersSheetName),
        Mappings.Where(mapping => mapping.Kind == "column")
            .Select(mapping => new LegacyImportColumnMapping(mapping.Scope!, mapping.TargetField, mapping.SourceColumn))
            .ToArray(),
        MachineMappings.Where(mapping => mapping.IsResolved)
            .Select(mapping => new LegacyImportMachineMapping(mapping.SectionKey!, NullIfBlank(mapping.SelectedMachineId)))
            .ToArray(),
        Rows.Where(row => row.Kind == "open_orders").Select(row => row.ToOpenOrderSelection()).ToArray(),
        Rows.Where(row => row.Kind == "planning").Select(row => row.ToPlanningSelection(
            MachineMappings.FirstOrDefault(mapping => mapping.SectionKey == row.SectionKey)?.SelectedMachineId)).ToArray());

    private IEnumerable<LegacyImportIssue> IssuesFor(string sheetName, int rowNumber) =>
        Issues.Where(issue => string.Equals(issue.SheetName, sheetName, StringComparison.OrdinalIgnoreCase)
            && issue.RowNumber == rowNumber);

    private bool HasGlobalServerBlockers() => Issues.Any(issue => !issue.RowNumber.HasValue
        && IsBlockingSeverity(issue.Severity));

    private bool HasUniqueColumnTargetFields() => Mappings
        .GroupBy(mapping => $"{mapping.Scope}:{mapping.TargetField}", StringComparer.OrdinalIgnoreCase)
        .All(group => group.Count() == 1);

    private bool CanPreview() => apiClient is not null && !IsBusy
        && workbookExists(SelectedFilePath)
        && SelectedFilePath.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase);

    private bool CanSkipRow(LegacyImportRowViewModel? row) => !IsBusy && IsEditor && row is not null;

    private void ClearPreview()
    {
        preview = null;
        expiryTimer.Stop();
        Mappings.Clear();
        MachineMappings.Clear();
        Rows.Clear();
        Issues.Clear();
        HeaderRowNumber = 0;
        if (!IsBusy)
        {
            Summary = "Choose an .xlsx workbook to create a Server preview.";
        }
    }

    internal void RowOrMappingChanged() => RaiseState();

    private void RaiseState()
    {
        OnPropertyChanged(nameof(CanCommit));
        OnPropertyChanged(nameof(ExpiresAt));
        OnPropertyChanged(nameof(TokenExpiryText));
        PreviewCommand.RaiseCanExecuteChanged();
        CommitCommand.RaiseCanExecuteChanged();
        SkipRowCommand.RaiseCanExecuteChanged();
        SkipAllUnresolvedCommand.RaiseCanExecuteChanged();
    }

    private static string SampleFor<T>(IReadOnlyList<T>? rows, string field) where T : class
    {
        if (rows is null || rows.Count == 0) return string.Empty;
        object? values = rows[0] switch
        {
            LegacyImportPlanningRow planning => planning.Values,
            LegacyImportOpenOrderRow openOrder => openOrder.Values,
            _ => default
        };
        var property = values?.GetType().GetProperties()
            .FirstOrDefault(candidate => string.Equals(candidate.Name, field, StringComparison.OrdinalIgnoreCase));
        return property?.GetValue(values)?.ToString() ?? string.Empty;
    }

    private static IReadOnlyList<string> ColumnChoicesFor(LegacyWorkingPlanPreview preview, string? sheetName)
    {
        var count = preview.Workbook.Sheets
            .FirstOrDefault(sheet => string.Equals(sheet.Name, sheetName, StringComparison.OrdinalIgnoreCase))
            ?.ColumnCount ?? 0;
        return Enumerable.Range(1, Math.Min(count, 16_384))
            .Select(ToExcelColumnName)
            .ToArray();
    }

    private static string ToExcelColumnName(int value)
    {
        Span<char> buffer = stackalloc char[3];
        var index = buffer.Length;
        while (value > 0)
        {
            value--;
            buffer[--index] = (char)('A' + (value % 26));
            value /= 26;
        }

        return new string(buffer[index..]);
    }

    private static bool IsBlockingSeverity(string severity) => string.Equals(severity, "blocking", StringComparison.OrdinalIgnoreCase);

    private static bool IsExpected(Exception exception) => exception is PlannerApiException
        or PlannerProtocolException or HttpRequestException or TaskCanceledException
        or IOException or UnauthorizedAccessException;

    private static string FriendlyMessage(Exception exception) => exception switch
    {
        TaskCanceledException => "The Server did not respond before the client timeout.",
        HttpRequestException => "The configured Server could not be reached.",
        PlannerApiException api => $"{api.Message} ({api.Code})",
        _ => exception.Message
    };

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string FormatRemaining(TimeSpan value) => value.TotalHours >= 1
        ? $"{(int)value.TotalHours}:{value.Minutes:D2}:{value.Seconds:D2}"
        : $"{value.Minutes}:{value.Seconds:D2}";

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

internal sealed class LegacyImportMappingViewModel : INotifyPropertyChanged
{
    private readonly LegacyExcelImportViewModel owner;
    private string targetField;
    private string sourceColumn = string.Empty;
    private string selectedMachineId = string.Empty;
    private LegacyImportMachineCandidate? selectedMachineCandidate;

    private LegacyImportMappingViewModel(LegacyExcelImportViewModel owner, string kind, string sourceHeader,
        string sampleValue, string targetField, bool required, decimal? candidateScore, string candidateReason)
    {
        this.owner = owner;
        Kind = kind;
        SourceHeader = sourceHeader;
        SampleValue = sampleValue;
        this.targetField = targetField;
        IsRequired = required;
        CandidateScore = candidateScore;
        CandidateReason = candidateReason;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string Kind { get; }
    public string? Scope { get; private init; }
    public string? SectionKey { get; private init; }
    public string SourceColumn
    {
        get => sourceColumn;
        set
        {
            var normalized = value?.Trim().ToUpperInvariant() ?? string.Empty;
            if (SetField(ref sourceColumn, normalized)) owner.RowOrMappingChanged();
        }
    }
    public string SourceHeader { get; }
    public string SampleValue { get; }
    public bool IsRequired { get; }
    public decimal? CandidateScore { get; }
    public string CandidateReason { get; }
    public IReadOnlyList<string> ColumnChoices { get; private init; } = [];
    public IReadOnlyList<LegacyImportMachineCandidate> MachineChoices { get; private init; } = [];
    public string TargetField => targetField;
    public string SelectedMachineId
    {
        get => selectedMachineId;
        set
        {
            if (SetField(ref selectedMachineId, value)) owner.RowOrMappingChanged();
        }
    }
    public LegacyImportMachineCandidate? SelectedMachineCandidate
    {
        get => selectedMachineCandidate;
        set
        {
            if (SetField(ref selectedMachineCandidate, value))
            {
                SelectedMachineId = value?.MachineId ?? string.Empty;
            }
        }
    }
    public string Decision => IsResolved ? "Selected" : "Choose";
    public bool IsResolved => Kind == "column"
        ? !string.IsNullOrWhiteSpace(TargetField)
            && ColumnChoices.Contains(SourceColumn, StringComparer.OrdinalIgnoreCase)
        : !string.IsNullOrWhiteSpace(SelectedMachineId);

    internal static LegacyImportMappingViewModel Column(string scope, LegacyImportColumnSuggestion suggestion,
        LegacyExcelImportViewModel owner, string sample, IReadOnlyList<string> columnChoices) => new(owner, "column", suggestion.Header ?? suggestion.Column ?? "(unlabeled)",
            sample, suggestion.Field, required: true, suggestion.Confidence, "Server suggestion")
        {
            Scope = scope,
            sourceColumn = suggestion.Column?.Trim().ToUpperInvariant() ?? string.Empty,
            ColumnChoices = columnChoices
        };

    internal static LegacyImportMappingViewModel Machine(LegacyImportMachineSection section,
        LegacyExcelImportViewModel owner) => new(owner, "machine", section.SourceLabel, string.Empty, string.Empty,
            required: true, section.Candidates.FirstOrDefault()?.Score, section.Candidates.FirstOrDefault()?.Reason ?? "Choose an existing Machine")
        {
            SectionKey = section.SectionKey,
            MachineChoices = section.Candidates ?? [],
        };

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Decision)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsResolved)));
        return true;
    }
}

internal sealed class LegacyImportRowViewModel : INotifyPropertyChanged
{
    private readonly LegacyExcelImportViewModel owner;
    private readonly IReadOnlyList<LegacyImportIssue> issues;
    private string decision = string.Empty;
    private bool isSkipped;
    private bool createBatch;
    private string existingOperation = string.Empty;
    private string routeOperation = string.Empty;
    private string caseId = string.Empty;
    private string caseSourceRowKey = string.Empty;
    private string machineId = string.Empty;
    private string batchNumber = string.Empty;
    private bool compatibilityOverrideConfirmed;
    private string compatibilityOverrideReason = string.Empty;
    private string skipReason = string.Empty;
    private string existingCaseId = string.Empty;
    private string newCasePartNumber = string.Empty;
    private string newCaseName = string.Empty;
    private string newCaseRevision = string.Empty;
    private string newCaseCustomer = string.Empty;
    private string newCaseCustomerReference = string.Empty;
    private string newCaseWorkingFolderPath = string.Empty;
    private string newCaseNotes = string.Empty;
    private string orderNumber = string.Empty;
    private string orderQuantity = string.Empty;
    private string orderWorkFinishDate = string.Empty;
    private string orderNotes = string.Empty;
    private LegacyImportCaseCandidate? selectedCaseCandidate;
    private LegacyImportCaseOperationCandidate? selectedRouteOperationCandidate;
    private LegacyImportBatchOperationCandidate? selectedExistingOperationCandidate;

    private LegacyImportRowViewModel(LegacyExcelImportViewModel owner, string kind, string rowKey, string sheetName,
        int rowNumber, string? sectionKey, IReadOnlyList<LegacyImportIssue> issues)
    {
        this.owner = owner;
        Kind = kind;
        RowKey = rowKey;
        SheetName = sheetName;
        RowNumber = rowNumber;
        SectionKey = sectionKey;
        this.issues = issues.ToArray();
        AddAllocationCommand = new AsyncCommand(AddAllocationAsync,
            () => Kind == "planning" && !IsSkipped);
        RemoveAllocationCommand = new AsyncCommand<LegacyImportAllocationViewModel>(RemoveAllocationAsync,
            allocation => allocation is not null && Allocations.Contains(allocation));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string Kind { get; }
    public string RowKey { get; }
    public string SheetName { get; }
    public int RowNumber { get; }
    public string? SectionKey { get; }
    public string? SourcePartNumber { get; private init; }
    public int? SourceQuantity { get; private init; }
    public string? SourceCustomer { get; private init; }
    public string? SourceReferenceOrOrderNumber { get; private init; }
    public string? PartNumber => SourcePartNumber;
    public int? PlannedQuantity => SourceQuantity;
    public string SourceSummary => string.Join(" · ", new[]
        {
            NullIfBlank(SourcePartNumber),
            SourceQuantity?.ToString(CultureInfo.InvariantCulture),
            NullIfBlank(SourceCustomer),
            NullIfBlank(SourceReferenceOrOrderNumber)
        }.Where(value => value is not null));
    public IReadOnlyList<LegacyImportIssue> Issues => issues;
    public IReadOnlyList<LegacyImportCaseCandidate> CaseCandidates { get; private init; } = [];
    public IReadOnlyList<LegacyImportOrderCandidate> OrderCandidates { get; private init; } = [];
    public IReadOnlyList<LegacyImportBatchCandidate> BatchCandidates { get; private init; } = [];
    public IReadOnlyList<LegacyImportCaseOperationCandidate> RouteOperationCandidates { get; private init; } = [];
    public IReadOnlyList<LegacyImportBatchOperationCandidate> ExistingOperationCandidates { get; private init; } = [];
    public IReadOnlyList<LegacyImportBatchOperationCandidate> AvailableExistingOperationCandidates =>
        ExistingOperationCandidates.Where(candidate => !candidate.IsAlreadyAssigned).ToArray();
    public ObservableCollection<LegacyImportAllocationViewModel> Allocations { get; } = [];
    public AsyncCommand AddAllocationCommand { get; }
    public AsyncCommand<LegacyImportAllocationViewModel> RemoveAllocationCommand { get; }
    public IReadOnlyList<string> SkipChoices { get; } = ["Skip this source row"];
    public string Message => issues.Count == 0 ? "No Server issue reported." : string.Join(" ", issues.Select(issue => issue.Message));
    public string Status => IsSkipped ? "Skip"
        : issues.Any(issue => string.Equals(issue.Severity, "blocking", StringComparison.OrdinalIgnoreCase)) ? "Blocked"
        : issues.Any(issue => string.Equals(issue.Severity, "warning", StringComparison.OrdinalIgnoreCase)) ? "Warning"
        : IsResolved ? "Ready" : "Blocked";
    public bool HasExplicitDecision => !string.IsNullOrWhiteSpace(Decision);
    public bool IsResolved => IsSkipped || (!issues.Any(issue => string.Equals(issue.Severity, "blocking", StringComparison.OrdinalIgnoreCase))
        && HasCompleteDecision());
    public IReadOnlyList<string> ActionChoices => Kind == "planning"
        ? ["skip", "assign_existing_operation", "create_batch_and_assign"]
        : ["skip", "create_case", "create_order"];
    public string Decision { get => decision; set => SetDecision(value); }
    public bool IsSkipped { get => isSkipped; set { if (SetField(ref isSkipped, value)) SetDecision(value ? "skip" : string.Empty); } }
    public bool CreateBatch { get => createBatch; set { if (SetField(ref createBatch, value)) SetDecision(value ? "create_batch_and_assign" : string.Empty); } }
    public string ExistingOperation { get => existingOperation; set { if (SetField(ref existingOperation, value) && !string.IsNullOrWhiteSpace(value)) SetDecision("assign_existing_operation"); } }
    public string RouteOperation { get => routeOperation; set => SetField(ref routeOperation, value); }
    public string CaseId { get => caseId; set => SetField(ref caseId, value); }
    public string CaseSourceRowKey { get => caseSourceRowKey; set => SetField(ref caseSourceRowKey, value); }
    public string MachineId { get => machineId; set => SetField(ref machineId, value); }
    public string BatchNumber { get => batchNumber; set => SetField(ref batchNumber, value); }
    public bool CompatibilityOverrideConfirmed { get => compatibilityOverrideConfirmed; set => SetField(ref compatibilityOverrideConfirmed, value); }
    public string CompatibilityOverrideReason { get => compatibilityOverrideReason; set => SetField(ref compatibilityOverrideReason, value); }
    public string SkipReason { get => skipReason; set => SetField(ref skipReason, value); }
    public string ExistingCaseId { get => existingCaseId; set => SetField(ref existingCaseId, value); }
    public LegacyImportCaseCandidate? SelectedCaseCandidate
    {
        get => selectedCaseCandidate;
        set
        {
            if (SetField(ref selectedCaseCandidate, value) && value is not null)
            {
                CaseId = value.CaseId;
                ExistingCaseId = value.CaseId;
            }
        }
    }
    public LegacyImportCaseOperationCandidate? SelectedRouteOperationCandidate
    {
        get => selectedRouteOperationCandidate;
        set
        {
            if (SetField(ref selectedRouteOperationCandidate, value) && value is not null)
            {
                CaseId = value.CaseId;
                RouteOperation = value.CaseOperationId;
            }
        }
    }
    public LegacyImportBatchOperationCandidate? SelectedExistingOperationCandidate
    {
        get => selectedExistingOperationCandidate;
        set
        {
            if (SetField(ref selectedExistingOperationCandidate, value) && value is not null)
            {
                ExistingOperation = value.BatchOperationId;
            }
        }
    }
    public string NewCasePartNumber { get => newCasePartNumber; set => SetField(ref newCasePartNumber, value); }
    public string NewCaseName { get => newCaseName; set => SetField(ref newCaseName, value); }
    public string NewCaseRevision { get => newCaseRevision; set => SetField(ref newCaseRevision, value); }
    public string NewCaseCustomer { get => newCaseCustomer; set => SetField(ref newCaseCustomer, value); }
    public string NewCaseCustomerReference { get => newCaseCustomerReference; set => SetField(ref newCaseCustomerReference, value); }
    public string NewCaseWorkingFolderPath { get => newCaseWorkingFolderPath; set => SetField(ref newCaseWorkingFolderPath, value); }
    public string NewCaseNotes { get => newCaseNotes; set => SetField(ref newCaseNotes, value); }
    public string OrderNumber { get => orderNumber; set => SetField(ref orderNumber, value); }
    public string OrderQuantity { get => orderQuantity; set => SetField(ref orderQuantity, value); }
    public string OrderWorkFinishDate { get => orderWorkFinishDate; set => SetField(ref orderWorkFinishDate, value); }
    public string OrderNotes { get => orderNotes; set => SetField(ref orderNotes, value); }

    internal static LegacyImportRowViewModel Planning(LegacyImportPlanningRow row,
        IEnumerable<LegacyImportIssue> issues, LegacyExcelImportViewModel owner) => new(owner, "planning", row.RowKey, row.SheetName, row.RowNumber, row.SectionKey, issues.ToArray())
        {
            CaseCandidates = row.Candidates?.Cases ?? [],
            OrderCandidates = row.Candidates?.Orders ?? [],
            BatchCandidates = row.Candidates?.Batches ?? [],
            RouteOperationCandidates = row.Candidates?.CaseOperations ?? [],
            ExistingOperationCandidates = row.Candidates?.BatchOperations ?? [],
            SourcePartNumber = row.Values?.PartNumber,
            SourceQuantity = row.Values?.Quantity,
            SourceCustomer = row.Values?.Customer,
            SourceReferenceOrOrderNumber = row.Values?.CaseReference
        };

    internal static LegacyImportRowViewModel OpenOrder(LegacyImportOpenOrderRow row,
        IEnumerable<LegacyImportIssue> issues, LegacyExcelImportViewModel owner) => new(owner, "open_orders", row.RowKey, row.SheetName, row.RowNumber, null, issues.ToArray())
        {
            CaseCandidates = row.Candidates?.Cases ?? [],
            OrderCandidates = row.Candidates?.Orders ?? [],
            SourcePartNumber = row.Values?.PartNumber,
            SourceQuantity = row.Values?.OutstandingQuantity ?? row.Values?.OrderedQuantity,
            SourceCustomer = row.Values?.Customer,
            SourceReferenceOrOrderNumber = row.Values?.OrderNumber ?? row.Values?.CaseReference
        };

    internal LegacyImportOpenOrderSelection ToOpenOrderSelection() => Decision switch
    {
        "skip" => new LegacyImportOpenOrderSelection(RowKey, "skip", null, null, null),
        "create_case" => new LegacyImportOpenOrderSelection(RowKey, "create_case", null, BuildNewCase(), BuildOrder()),
        "create_order" => new LegacyImportOpenOrderSelection(RowKey, "create_order", NullIfBlank(ExistingCaseId), null, BuildOrder()),
        _ => new LegacyImportOpenOrderSelection(RowKey, Decision, NullIfBlank(ExistingCaseId), BuildNewCase(), BuildOrder())
    };

    internal LegacyImportPlanningSelection ToPlanningSelection(string? mappedMachineId) => new(
        RowKey, Decision, NullIfBlank(ExistingOperation), NullIfBlank(CaseId), NullIfBlank(CaseSourceRowKey), NullIfBlank(RouteOperation),
        NullIfBlank(BatchNumber), Allocations.Select(allocation => allocation.ToContract()).ToArray(), NullIfBlank(MachineId) ?? NullIfBlank(mappedMachineId),
        CompatibilityOverrideConfirmed
            ? new LegacyImportCompatibilityOverride(true, NullIfBlank(CompatibilityOverrideReason))
            : null);

    internal Task AddAllocationAsync()
    {
        Allocations.Add(new LegacyImportAllocationViewModel(OrderCandidates, () =>
        {
            owner.RowOrMappingChanged();
            RaiseStateProperties();
        }));
        RaiseStateProperties();
        return Task.CompletedTask;
    }

    internal Task RemoveAllocationAsync(LegacyImportAllocationViewModel? allocation)
    {
        if (allocation is not null)
        {
            Allocations.Remove(allocation);
            RaiseStateProperties();
        }

        return Task.CompletedTask;
    }

    private bool HasCompleteDecision() => Kind == "planning"
        ? Decision switch
        {
            "assign_existing_operation" => SelectedExistingOperationCandidate is { IsAlreadyAssigned: false },
            "create_batch_and_assign" => SelectedCaseCandidate is not null
                && SelectedRouteOperationCandidate is not null
                && !string.IsNullOrWhiteSpace(BatchNumber)
                && HasCompleteAllocations()
                && (!CompatibilityOverrideConfirmed || !string.IsNullOrWhiteSpace(CompatibilityOverrideReason)),
            _ => false
        }
        : Decision switch
        {
            "create_case" => !string.IsNullOrWhiteSpace(NewCasePartNumber)
                && !string.IsNullOrWhiteSpace(NewCaseName)
                && !string.IsNullOrWhiteSpace(NewCaseWorkingFolderPath)
                && (!HasAnyOrderInput || HasCompleteOrderInput),
            "create_order" => SelectedCaseCandidate is not null
                && HasCompleteOrderInput,
            _ => false
        };

    private LegacyImportNewCase? BuildNewCase() => !HasAnyNewCaseInput
        ? null
        : new LegacyImportNewCase(NullIfBlank(NewCasePartNumber), NullIfBlank(NewCaseName), NullIfBlank(NewCaseRevision),
            NullIfBlank(NewCaseCustomer), NullIfBlank(NewCaseCustomerReference), NullIfBlank(NewCaseWorkingFolderPath), NullIfBlank(NewCaseNotes));

    private LegacyImportOrderInput? BuildOrder() => !HasAnyOrderInput
        ? null
        : new LegacyImportOrderInput(NullIfBlank(OrderNumber), ParseInt(OrderQuantity), NullIfBlank(OrderWorkFinishDate), NullIfBlank(OrderNotes));

    public bool HasAnyNewCaseInput => !string.IsNullOrWhiteSpace(NewCasePartNumber)
        || !string.IsNullOrWhiteSpace(NewCaseName)
        || !string.IsNullOrWhiteSpace(NewCaseRevision)
        || !string.IsNullOrWhiteSpace(NewCaseCustomer)
        || !string.IsNullOrWhiteSpace(NewCaseCustomerReference)
        || !string.IsNullOrWhiteSpace(NewCaseWorkingFolderPath)
        || !string.IsNullOrWhiteSpace(NewCaseNotes);

    public bool HasAnyOrderInput => !string.IsNullOrWhiteSpace(OrderNumber)
        || !string.IsNullOrWhiteSpace(OrderQuantity)
        || !string.IsNullOrWhiteSpace(OrderWorkFinishDate)
        || !string.IsNullOrWhiteSpace(OrderNotes);

    public bool HasCompleteOrderInput => !string.IsNullOrWhiteSpace(OrderNumber)
        && ParseInt(OrderQuantity) is > 0
        && DateOnly.TryParseExact(OrderWorkFinishDate?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out _);

    private bool HasCompleteAllocations()
    {
        if (Allocations.Count == 0 || Allocations.Any(allocation => !allocation.IsComplete))
        {
            return false;
        }

        var keys = Allocations.Select(allocation => allocation.SemanticKey!).ToArray();
        if (keys.Distinct(StringComparer.Ordinal).Count() != keys.Length)
        {
            return false;
        }

        if (!SourceQuantity.HasValue)
        {
            return true;
        }

        return Allocations.Sum(allocation => (long)allocation.ParsedQuantity!.Value) == SourceQuantity.Value;
    }

    private void SetDecision(string value)
    {
        if (SetField(ref decision, value))
        {
            isSkipped = string.Equals(value, "skip", StringComparison.OrdinalIgnoreCase);
            createBatch = string.Equals(value, "create_batch_and_assign", StringComparison.OrdinalIgnoreCase);
            RaiseStateProperties();
        }
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        owner.RowOrMappingChanged();
        RaiseStateProperties();
        return true;
    }

    private void RaiseStateProperties()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasExplicitDecision)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasAnyOrderInput)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasCompleteOrderInput)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasAnyNewCaseInput)));
        AddAllocationCommand.RaiseCanExecuteChanged();
        RemoveAllocationCommand.RaiseCanExecuteChanged();
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static int? ParseInt(string? value) => int.TryParse(value, NumberStyles.Integer,
        CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
}

internal sealed class LegacyImportAllocationViewModel : INotifyPropertyChanged
{
    private readonly Action changed;
    private string type = "order";
    private string orderId = string.Empty;
    private string orderSourceRowKey = string.Empty;
    private string quantity = string.Empty;
    private LegacyImportOrderCandidate? selectedOrderCandidate;

    internal LegacyImportAllocationViewModel(
        IReadOnlyList<LegacyImportOrderCandidate> orderCandidates,
        Action changed)
    {
        OrderCandidates = orderCandidates;
        this.changed = changed;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public IReadOnlyList<string> TypeChoices { get; } = ["order", "stock", "scrapAllowance"];
    public IReadOnlyList<LegacyImportOrderCandidate> OrderCandidates { get; }
    public string Type { get => type; set => SetField(ref type, value); }
    public string OrderId { get => orderId; set => SetField(ref orderId, value); }
    public string OrderSourceRowKey { get => orderSourceRowKey; set => SetField(ref orderSourceRowKey, value); }
    public string Quantity { get => quantity; set => SetField(ref quantity, value); }
    public LegacyImportOrderCandidate? SelectedOrderCandidate
    {
        get => selectedOrderCandidate;
        set
        {
            if (SetField(ref selectedOrderCandidate, value))
            {
                OrderId = value?.OrderId ?? string.Empty;
            }
        }
    }

    internal LegacyImportAllocation ToContract() => new(
        Type,
        NullIfBlank(OrderId),
        NullIfBlank(OrderSourceRowKey),
        ParsedQuantity);

    public int? ParsedQuantity => int.TryParse(Quantity, NumberStyles.Integer,
        CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    public bool IsComplete => Type switch
    {
        "order" => ParsedQuantity is > 0 && HasExactlyOneOrderReference,
        "stock" or "scrapAllowance" => ParsedQuantity is > 0 && !HasOrderReference,
        _ => false
    };

    internal string? SemanticKey => !IsComplete
        ? null
        : Type == "order"
            ? $"order:{SelectedOrderCandidate?.OrderId ?? OrderSourceRowKey.Trim()}"
            : Type;

    private bool HasOrderReference => SelectedOrderCandidate is not null
        || !string.IsNullOrWhiteSpace(OrderId)
        || !string.IsNullOrWhiteSpace(OrderSourceRowKey);

    private bool HasExactlyOneOrderReference
    {
        get
        {
            var hasSelectedExisting = SelectedOrderCandidate is not null
                && string.Equals(OrderId, SelectedOrderCandidate.OrderId, StringComparison.Ordinal);
            var hasSource = !string.IsNullOrWhiteSpace(OrderSourceRowKey);
            if (hasSelectedExisting)
            {
                return !hasSource;
            }

            return hasSource
                && SelectedOrderCandidate is null
                && string.IsNullOrWhiteSpace(OrderId);
        }
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ParsedQuantity)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsComplete)));
        changed();
        return true;
    }

    private static string? NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
