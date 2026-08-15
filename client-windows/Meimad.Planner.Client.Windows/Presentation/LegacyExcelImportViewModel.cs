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
/// Stages a legacy workbook for an explicit, server-validated import. It can
/// build a non-authoritative review draft from Server-parsed values and exact
/// candidates, while the final commit remains explicit and authoritative.
/// </summary>
internal sealed class LegacyExcelImportViewModel : INotifyPropertyChanged
{
    private static readonly (string Field, bool Required)[] PlanningColumnTargets =
    [
        ("customer", false),
        ("partNumber", true),
        ("caseReference", false),
        ("notes", false),
        ("quantity", true),
        ("materialStatus", false),
        ("startDate", false),
        ("endDate", false),
        ("plannerDeliveryDate", false),
        ("customerDeliveryDate", false)
    ];

    private static readonly (string Field, bool Required)[] OpenOrderColumnTargets =
    [
        ("partNumber", true),
        ("orderNumber", false),
        ("orderLine", false),
        ("customer", false),
        ("deliveryDate", false),
        ("revision", false),
        ("outstandingQuantity", false),
        ("notes", false),
        ("drawingNumber", false),
        ("caseReference", false),
        ("orderedQuantity", false),
        ("itemName", false),
        ("picturePath", false)
    ];

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
    private int wizardStep;
    private LegacyImportRowViewModel? selectedWizardRow;
    private string patternScope = "same_part_and_operation";
    private string patternApplicationSummary = string.Empty;
    private string machineSuggestionSummary = string.Empty;
    private string batchNumberTemplate = string.Empty;
    private bool importOrders;
    private bool importPoolBatches;
    private bool importMachineAssignments;
    private bool hasExplicitOutcomeSelection;
    private bool isApplyingPreview;
    private bool hasPendingPreviewCorrections;
    private bool hasSheetSelectionCorrections;
    private bool hasColumnMappingCorrections;
    private bool isPreparingAutomatically;
    private bool automaticPrepared;
    private bool confirmAutomaticSkips;
    private bool isSynchronizingCaseStageSelection;
    private int headerRowNumber;
    private string resultSummary = string.Empty;
    private string currentImportStage = "cases";
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
        CommitCommand = new AsyncCommand(CommitAsync, () => CanCommitNow);
        SkipRowCommand = new AsyncCommand<LegacyImportRowViewModel>(SkipRowAsync, CanSkipRow);
        SkipAllUnresolvedCommand = new AsyncCommand(SkipAllUnresolvedAsync,
            () => IsEditor && !IsBusy && preview is not null);
        NextStepCommand = new AsyncCommand(NextStepAsync, () => CanGoNext);
        PreviousStepCommand = new AsyncCommand(PreviousStepAsync, () => CanGoPrevious);
        ApplyPatternCommand = new AsyncCommand(ApplyPatternAsync, () => CanApplyPattern);
        ApplySelectedPatternToSimilarCommand = new AsyncCommand(ApplyPatternToSimilarAsync, () => CanApplyPattern);
        ApplySelectedPatternToAllCommand = new AsyncCommand(ApplyPatternToAllAsync, () => CanApplyPattern);
        AcceptClearMachineSuggestionsCommand = new AsyncCommand(AcceptClearMachineSuggestionsAsync,
            () => CanAcceptClearMachineSuggestions);
        PrepareAutomaticallyCommand = new AsyncCommand(PrepareAutomaticallyAsync,
            () => CanPrepareAutomatically);
        ShowCasesStageCommand = new AsyncCommand(() => ShowImportStageAsync("cases"));
        ShowOrdersStageCommand = new AsyncCommand(() => ShowImportStageAsync("orders"));
        ShowBatchesStageCommand = new AsyncCommand(() => ShowImportStageAsync("batches"));
        ShowAssignmentsStageCommand = new AsyncCommand(() => ShowImportStageAsync("assignments"));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised only after the Server accepts (or idempotently replays) a commit.</summary>
    public event EventHandler<LegacyWorkingPlanCommitReceipt>? ImportCommitted;

    public ObservableCollection<LegacyImportMappingViewModel> Mappings { get; } = [];

    public ObservableCollection<LegacyImportMappingViewModel> MachineMappings { get; } = [];

    public IEnumerable<LegacyImportMappingViewModel> IncludedMappings =>
        Mappings.Where(IsIncludedInSelectedOutcome);

    public IEnumerable<LegacyImportMappingViewModel> IncludedMachineMappings =>
        ImportMachineAssignments ? MachineMappings : [];

    public bool ShowsMachineMappings => ImportMachineAssignments;

    public ObservableCollection<LegacyImportRowViewModel> Rows { get; } = [];

    /// <summary>
    /// The preview remains one server-owned staging document. These filtered views only
    /// guide a planner through the independent decisions; they never create a second
    /// import or cause a commit before the final review.
    /// </summary>
    public IEnumerable<LegacyImportRowViewModel> PoolRows => Rows.Where(row => row.Kind == "planning");

    public IEnumerable<LegacyImportRowViewModel> OrderRows => Rows.Where(row => row.Kind == "open_orders");

    public IEnumerable<LegacyImportRowViewModel> BatchRows => Rows.Where(row => row.Kind == "planning");

    public IEnumerable<LegacyImportRowViewModel> AssignmentRows => Rows.Where(row => row.Kind == "planning");

    /// <summary>
    /// The Case stage deliberately shows one representative source row per Part Number.
    /// Orders and planning rows continue to retain their own source identity for the
    /// later stages and final atomic request.
    /// </summary>
    public IEnumerable<LegacyImportRowViewModel> CaseRows => OrderRows
        .GroupBy(row => string.IsNullOrWhiteSpace(row.SourcePartNumber)
            ? row.RowKey
            : row.SourcePartNumber.Trim(), StringComparer.OrdinalIgnoreCase)
        .Select(group => group.OrderBy(row => row.RowNumber).First());

    public IEnumerable<LegacyImportRowViewModel> CurrentStageRows => CurrentImportStage switch
    {
        "cases" => CaseRows,
        "orders" => OrderRows,
        "batches" => BatchRows,
        "assignments" => AssignmentRows,
        _ => []
    };

    public IEnumerable<LegacyImportRowViewModel> IncludedRows => Rows.Where(IsIncludedInSelectedOutcome);

    public IEnumerable<LegacyImportRowViewModel> ReviewRows =>
        Rows.Where(IsIncludedInSelectedOutcome).Where(row => row.HasExplicitDecision);

    public ObservableCollection<LegacyImportIssue> Issues { get; } = [];

    public IReadOnlyList<LegacyImportSheet> DetectedSheets => preview?.Workbook.Sheets ?? [];

    public IReadOnlyList<string> SheetChoices => DetectedSheets
        .Select(sheet => sheet.Name)
        .ToArray();

    public IReadOnlyList<string> OptionalSheetChoices => new[] { string.Empty }
        .Concat(SheetChoices)
        .ToArray();

    public AsyncCommand PreviewCommand { get; }

    public AsyncCommand CommitCommand { get; }

    public AsyncCommand<LegacyImportRowViewModel> SkipRowCommand { get; }

    public AsyncCommand SkipAllUnresolvedCommand { get; }

    public AsyncCommand NextStepCommand { get; }

    public AsyncCommand PreviousStepCommand { get; }

    public AsyncCommand ApplyPatternCommand { get; }

    public AsyncCommand ApplySelectedPatternToSimilarCommand { get; }

    public AsyncCommand ApplySelectedPatternToAllCommand { get; }

    public AsyncCommand AcceptClearMachineSuggestionsCommand { get; }

    public AsyncCommand PrepareAutomaticallyCommand { get; }

    public AsyncCommand ShowCasesStageCommand { get; }

    public AsyncCommand ShowOrdersStageCommand { get; }

    public AsyncCommand ShowBatchesStageCommand { get; }

    public AsyncCommand ShowAssignmentsStageCommand { get; }

    public IReadOnlyList<LegacyImportChoice> PatternScopeChoices { get; } =
    [
        new("same_machine_section", "Same Machine section"),
        new("same_part_and_operation", "Same Part and operation shape"),
        new("all_eligible_rows", "All eligible rows")
    ];

    public string CurrentImportStage
    {
        get => currentImportStage;
        private set
        {
            if (SetField(ref currentImportStage, value))
            {
                OnPropertyChanged(nameof(CurrentImportStageTitle));
                OnPropertyChanged(nameof(CurrentImportStageDescription));
                OnPropertyChanged(nameof(CurrentStageRows));
            }
        }
    }

    public string CurrentImportStageTitle => CurrentImportStage switch
    {
        "cases" => "Step 1 — Cases in the Case Pool",
        "orders" => "Step 2 — Find and import related Orders",
        "batches" => "Step 3 — Create Batches in the Pool",
        "assignments" => "Step 4 — Assign Batches to Machines",
        _ => "Import stage"
    };

    public string CurrentImportStageDescription => CurrentImportStage switch
    {
        "cases" => "Match each Part Number to an existing Case or create its Case record. No Batch or Machine choice is made here.",
        "orders" => "Review Orders under their selected Case. Existing Orders stay unchanged; create only the missing Orders you approve.",
        "batches" => "Create full-route Batches in the unassigned Pool. A Case needs an existing complete route before it can produce a Batch.",
        "assignments" => "For the Pool Batches you want to dispatch now, choose one compatible route Operation and an existing Machine. Leave the rest in Pool.",
        _ => string.Empty
    };

    public int WizardStep
    {
        get => wizardStep;
        private set
        {
            if (SetField(ref wizardStep, value))
            {
                RaiseWizardState();
            }
        }
    }

    public string WizardStepTitle => WizardStep switch
    {
        0 => "1. Preview workbook",
        1 => "2. Choose import outcomes",
        2 => "3. Map source columns and Machines",
        3 => "4. Resolve rows and apply patterns",
        _ => "5. Review and commit"
    };

    public bool IsPreviewStep => WizardStep == 0;
    public bool IsOutcomesStep => WizardStep == 1;
    public bool IsSourceMappingStep => WizardStep == 2;
    public bool IsResolutionStep => WizardStep == 3;
    // Kept as aliases for the focused row sections in the resolution step.
    public bool IsOrdersStep => IsResolutionStep;
    public bool IsPoolStep => IsResolutionStep;
    public bool IsAssignmentStep => IsResolutionStep;
    public bool IsReviewStep => WizardStep == 4;
    public bool CanCommitNow => IsReviewStep && CanCommit;
    public bool CanGoNext => preview is not null && !IsBusy && !HasPendingPreviewCorrections && WizardStep < 4 && (WizardStep switch
    {
        0 => true,
        1 => HasSelectedOutcome,
        2 => HasResolvedMappings && !HasPendingPreviewCorrections,
        3 => HasResolvedIncludedRows,
        _ => false
    });
    public bool CanGoPrevious => !IsBusy && WizardStep > 0;

    public LegacyImportRowViewModel? SelectedWizardRow
    {
        get => selectedWizardRow;
        set
        {
            if (SetField(ref selectedWizardRow, value))
            {
                OnPropertyChanged(nameof(CanApplyPattern));
                ApplyPatternCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string PatternScope
    {
        get => patternScope;
        set
        {
            if (SetField(ref patternScope, value))
            {
                OnPropertyChanged(nameof(PatternPreviewText));
            }
        }
    }

    public string BatchNumberTemplate
    {
        get => batchNumberTemplate;
        set => SetField(ref batchNumberTemplate, value);
    }

    public string PatternApplicationSummary
    {
        get => patternApplicationSummary;
        private set => SetField(ref patternApplicationSummary, value);
    }

    public string MachineSuggestionSummary
    {
        get => machineSuggestionSummary;
        private set => SetField(ref machineSuggestionSummary, value);
    }

    public bool CanAcceptClearMachineSuggestions => IsEditor && !IsBusy
        && MachineMappings.Any(mapping => !mapping.IsResolved && mapping.HasClearMachineSuggestion);

    public bool CanPrepareAutomatically => preview is not null
        && !IsBusy
        && !HasPendingPreviewCorrections;

    public bool AutomaticPrepared
    {
        get => automaticPrepared;
        private set => SetField(ref automaticPrepared, value);
    }

    public bool ConfirmAutomaticSkips
    {
        get => confirmAutomaticSkips;
        set
        {
            if (SetField(ref confirmAutomaticSkips, value))
            {
                RaiseState();
            }
        }
    }

    public bool RequiresAutomaticSkipConfirmation => AutomaticPrepared && AutomaticSkippedRows > 0;

    public int AutomaticReadyRows => !AutomaticPrepared
        ? 0
        : Rows.Count(row => IsIncludedInSelectedOutcome(row) && row.IsResolved && row.IsMutation);

    public int AutomaticSkippedRows => !AutomaticPrepared
        ? 0
        : Rows.Count(row => IsIncludedInSelectedOutcome(row) && row.IsSkipped);

    public IReadOnlyList<LegacyImportRowViewModel> AutomaticAttentionRows => !AutomaticPrepared
        ? []
        : Rows
            .Where(IsIncludedInSelectedOutcome)
            .Where(row => !row.IsResolved
                || row.IsSkipped && !string.IsNullOrWhiteSpace(row.AutomaticReason))
            .ToArray();

    public string AutomaticImportSummary
    {
        get
        {
            if (!AutomaticPrepared)
            {
                return preview is null
                    ? "Preview a workbook before preparing an automatic draft."
                    : "The preview is ready. Prepare an automatic draft to fill every safe decision.";
            }

            var selected = Rows.Where(IsIncludedInSelectedOutcome).ToArray();
            var orders = selected.Count(row => row.Decision == "create_order");
            var pool = selected.Count(row => row.Decision == "create_batch_to_pool");
            var assignments = selected.Count(row => row.Decision == "create_batch_and_assign");
            var stock = pool + assignments;
            return $"Automatic draft: {orders} Order(s), {pool} Pool Batch(es), "
                + $"{assignments} Batch-and-Machine assignment(s), and {stock} full-quantity stock allocation(s). "
                + $"{AutomaticSkippedRows} row(s) were safely skipped"
                + (AutomaticSkippedRows > 0
                    ? " and require explicit confirmation before import; "
                    : "; ")
                + $"{AutomaticAttentionRows.Count} row(s) still need attention. Nothing has been written.";
        }
    }

    public bool CanApplyPattern => IsEditor && !IsBusy
        && SelectedWizardRow is { IsSkipped: false, IsResolved: true, IsMutation: true }
        // New Case details are row-specific, while a persisted Batch Operation is a
        // one-to-one consumable assignment target. Only these reusable decisions are
        // eligible for Similar/All expansion.
        && SelectedWizardRow.Decision is "create_order" or "create_batch_to_pool" or "create_batch_and_assign";

    public bool ImportOrders
    {
        get => importOrders;
        set => SetOutcome(ref importOrders, value);
    }

    public bool ImportPoolBatches
    {
        get => importPoolBatches;
        set => SetOutcome(ref importPoolBatches, value);
    }

    public bool ImportMachineAssignments
    {
        get => importMachineAssignments;
        set => SetOutcome(ref importMachineAssignments, value);
    }

    public LegacyImportRowViewModel? SelectedRow
    {
        get => SelectedWizardRow;
        set => SelectedWizardRow = value;
    }

    public string PatternPreviewText => SelectedWizardRow is null
        ? "Select a resolved row, choose a scope, then apply only its explicit choices to matching rows. Nothing is saved until Review and commit."
        : SelectedWizardRow.Decision is "assign_existing_operation" or "create_case"
            ? SelectedWizardRow.Decision == "assign_existing_operation"
                ? "An existing Batch Operation can be assigned only once. Select a different unassigned Operation for each source row; this action cannot be copied as a pattern."
                : "New Case identity, folder, and optional Order values are row-specific. Resolve each new Case individually; this action cannot be copied as a pattern."
            : $"Apply the explicit choices from row {SelectedWizardRow.RowNumber} to {PatternScopeDescription}. IDs are copied only when the target preview offers the same reusable candidate; unresolved fields stay unresolved.";

    public string ValidationSummary
    {
        get
        {
            var rows = Rows.Where(IsIncludedInSelectedOutcome).ToArray();
            var ready = rows.Count(row => row.IsResolved && row.IsMutation);
            var skipped = rows.Count(row => row.IsSkipped);
            var unresolved = rows.Count(row => !row.IsResolved);
            return $"{rows.Length} selected-outcome rows: {ready} ready to import, {skipped} skipped, {unresolved} still need a decision.";
        }
    }

    public string PreviewSummary
    {
        get
        {
            if (preview is null)
            {
                return "No workbook preview is loaded.";
            }

            var blockingRows = CountIssueRows("blocking");
            var warningRows = CountIssueRows("warning");
            var validRows = Math.Max(0, Rows.Count - blockingRows);
            var selectedRows = Rows.Where(IsIncludedInSelectedOutcome).ToArray();
            var skippedRows = selectedRows.Count(row => row.IsSkipped);
            var caseCreates = selectedRows.Count(row => row.Decision == "create_case");
            var orderCreates = selectedRows.Count(row => row.Decision == "create_order"
                || row.Decision == "create_case" && row.IncludeOrderWithNewCase);
            var batchCreates = selectedRows.Count(row => row.Decision is "create_batch_to_pool" or "create_batch_and_assign");
            var batchOperations = selectedRows.Where(row => row.Decision is "create_batch_to_pool" or "create_batch_and_assign")
                .Sum(row => row.SelectedCaseRouteOperationCount);
            var assignments = selectedRows.Count(row => row.Decision is "create_batch_and_assign" or "assign_existing_operation");
            var caseMatches = Rows.Count(row => row.CaseCandidates.Count > 0);
            var orderMatches = Rows.Count(row => row.OrderCandidates.Count > 0);
            var unknownMachines = MachineMappings.Count(mapping => !mapping.HasSourceMachineMatch);
            var duplicateIndicators = Issues.Count(issue => issue.Code.Contains("duplicate", StringComparison.OrdinalIgnoreCase));
            var globalBlockers = Issues.Count(issue => !issue.RowNumber.HasValue && IsBlockingSeverity(issue.Severity));

            return $"Detected {Rows.Count} rows: {validRows} without row blockers, {warningRows} warning row(s), "
                + $"{blockingRows} error row(s), {globalBlockers} global blocker(s), {skippedRows} selected row(s) skipped. "
                + $"Selected decisions: {caseCreates} Case(s), {orderCreates} Order(s), {batchCreates} Batch(es), "
                + $"{batchOperations} route Batch Operation(s), {assignments} Machine assignment(s). "
                + $"Existing-match indicators: {caseMatches} Case row(s), {orderMatches} Order row(s); "
                + $"{unknownMachines} unmatched Machine section(s), {duplicateIndicators} duplicate indicator(s).";
        }
    }

    public bool HasPendingPreviewCorrections => hasPendingPreviewCorrections;

    public string PreviewCorrectionStatus => !HasPendingPreviewCorrections
        ? "The displayed rows match the selected sheets and column mappings."
        : "Sheet or column choices changed. Validate / refresh the preview before continuing or committing.";

    public string PreviewActionText => preview is null
        ? "Preview workbook"
        : HasPendingPreviewCorrections ? "Validate corrections" : "Refresh preview";

    public string ResultSummary
    {
        get => resultSummary;
        private set
        {
            if (SetField(ref resultSummary, value))
            {
                OnPropertyChanged(nameof(HasResultSummary));
            }
        }
    }

    public bool HasResultSummary => !string.IsNullOrWhiteSpace(ResultSummary);

    public string OutcomeSummary => !HasSelectedOutcome
        ? "Choose at least one import outcome before continuing."
        : $"Selected: {(ImportOrders ? "Orders" : string.Empty)}{(ImportOrders && (ImportPoolBatches || ImportMachineAssignments) ? ", " : string.Empty)}{(ImportPoolBatches ? "unassigned pool Batches" : string.Empty)}{(ImportPoolBatches && ImportMachineAssignments ? ", " : string.Empty)}{(ImportMachineAssignments ? "Machine assignments" : string.Empty)}. Sheets outside these outcomes are omitted from the one atomic commit.";

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
                if (preview is not null && !isApplyingPreview) hasSheetSelectionCorrections = true;
                MarkPreviewCorrectionPending();
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
                if (preview is not null && !isApplyingPreview) hasSheetSelectionCorrections = true;
                MarkPreviewCorrectionPending();
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
        && !HasPendingPreviewCorrections
        && (!RequiresAutomaticSkipConfirmation || ConfirmAutomaticSkips)
        && !HasGlobalServerBlockers()
        && Mappings.Where(IsIncludedInSelectedOutcome).All(mapping => mapping.IsResolved)
        && HasUniqueColumnTargetFields()
        && MachineMappings.Where(mapping => Rows.Any(row => row.RequiresMachineMapping
            && row.SectionKey == mapping.SectionKey)).All(mapping => mapping.IsResolved)
        && Rows.Where(IsIncludedInSelectedOutcome).All(row => row.HasExplicitDecision && row.IsResolved)
        && Rows.Any(row => IsIncludedInSelectedOutcome(row) && row.IsMutation && row.IsResolved);

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

        if (preview is not null)
        {
            // A refresh validates the exact bytes, sheets, and mappings again. Once it
            // starts, the prior token must never remain commit-eligible if reading or
            // Server validation fails.
            hasPendingPreviewCorrections = true;
            ResultSummary = string.Empty;
            Summary = "Refreshing the Server preview. Commit remains blocked until this validation succeeds.";
            RaiseState();
        }

        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            await using var workbook = openWorkbook(SelectedFilePath);
            var result = preview is null
                ? await apiClient!.PreviewLegacyWorkingPlanAsync(
                    workbook,
                    Path.GetFileName(SelectedFilePath))
                : await apiClient!.PreviewLegacyWorkingPlanAsync(
                    workbook,
                    Path.GetFileName(SelectedFilePath),
                    NullIfBlank(SourceSheetName),
                    OpenOrdersSheetName.Trim(),
                    hasSheetSelectionCorrections && !hasColumnMappingCorrections
                        ? null
                        : BuildPreviewColumnMappings());
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
            ResultSummary = BuildResultSummary(receipt);
            Summary = receipt.Replayed
                ? "The Server replayed the already accepted import receipt; no duplicate write was performed."
                : "The Server accepted the validated import atomically. See the result summary below.";
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

        foreach (var row in Rows.Where(IsIncludedInSelectedOutcome).Where(row => !row.IsResolved))
        {
            row.IsSkipped = true;
        }

        return Task.CompletedTask;
    }

    private Task NextStepAsync()
    {
        if (CanGoNext)
        {
            WizardStep++;
        }

        return Task.CompletedTask;
    }

    private Task PreviousStepAsync()
    {
        if (CanGoPrevious)
        {
            WizardStep--;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Applies a planner-selected pattern only to unresolved preview rows. It copies an
    /// existing entity reference only if the exact stable ID is present in the target
    /// row's Server preview. New Case details, Order/date values and Machine mappings
    /// are never inferred or copied. The only reusable allocation shape is one complete
    /// stock/scrap line, recalculated from each destination row's own quantity; optional
    /// Batch numbers come solely from the planner-entered template.
    /// </summary>
    private Task ApplyPatternAsync()
    {
        var source = SelectedWizardRow;
        if (!CanApplyPattern || source is null)
        {
            return Task.CompletedTask;
        }

        var targets = Rows.Where(target => IsEligiblePatternTarget(source, target)).ToArray();
        var appliedTargets = targets.Where(target => target.ApplyExplicitPatternFrom(source)).ToArray();
        var applied = appliedTargets.Length;
        var leftUnchanged = targets.Length - applied;
        var reservedBatchNumbers = Rows.Where(row => !string.IsNullOrWhiteSpace(row.BatchNumber))
            .Select(row => row.BatchNumber.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var templateApplied = 0;
        var templateCollisions = 0;
        if (!string.IsNullOrWhiteSpace(BatchNumberTemplate))
        {
            foreach (var target in appliedTargets)
            {
                if (target.TryApplyBatchNumberTemplate(BatchNumberTemplate, reservedBatchNumbers)) templateApplied++;
                else if (target.Decision is "create_batch_to_pool" or "create_batch_and_assign") templateCollisions++;
            }
        }
        var needsReview = appliedTargets.Count(target => !target.IsResolved);

        PatternApplicationSummary = targets.Length == 0
            ? "No unresolved rows matched this pattern. Existing choices were left unchanged."
            : $"Applied the explicit action from row {source.RowNumber} to {applied} unresolved preview row(s); {needsReview} need row-specific review, and {leftUnchanged} were left unchanged because the exact Server candidate was not offered. Batch template applied to {templateApplied}; {templateCollisions} generated blank or duplicate values. Order numbers, dates and Machine mappings are never copied. Review remaining fields before committing.";
        RowOrMappingChanged();
        return Task.CompletedTask;
    }

    private Task ApplyPatternToSimilarAsync()
    {
        PatternScope = "same_part_and_operation";
        return ApplyPatternAsync();
    }

    private Task ApplyPatternToAllAsync()
    {
        PatternScope = "all_eligible_rows";
        return ApplyPatternAsync();
    }

    private Task AcceptClearMachineSuggestionsAsync()
    {
        var mappings = MachineMappings.Where(mapping => !mapping.IsResolved).ToArray();
        var accepted = mappings.Count(mapping => mapping.AcceptClearMachineSuggestion());
        MachineSuggestionSummary = accepted == 0
            ? "No Machine suggestions met the explicit high-confidence rule. Choose each Machine manually."
            : $"Accepted {accepted} clear Machine suggestion(s) after your approval. {mappings.Length - accepted} section(s) still require a manual choice.";
        RowOrMappingChanged();
        return Task.CompletedTask;
    }

    internal Task PrepareAutomaticallyAsync()
    {
        if (!CanPrepareAutomatically)
        {
            return Task.CompletedTask;
        }

        isPreparingAutomatically = true;
        try
        {
            // Outcomes describe what the preview actually contains. They are local draft
            // choices only; the explicit Commit command remains the only write boundary.
            if (!hasExplicitOutcomeSelection)
            {
                importOrders = !string.IsNullOrWhiteSpace(OpenOrdersSheetName)
                    && Rows.Any(row => row.Kind == "open_orders");
                var hasPlanningRows = !string.IsNullOrWhiteSpace(SourceSheetName)
                    && Rows.Any(row => row.Kind == "planning");
                importPoolBatches = hasPlanningRows;
                importMachineAssignments = hasPlanningRows;
            }
            OnPropertyChanged(nameof(ImportOrders));
            OnPropertyChanged(nameof(ImportPoolBatches));
            OnPropertyChanged(nameof(ImportMachineAssignments));
            foreach (var row in Rows)
            {
                row.NotifyOutcomeSelectionChanged();
            }

            var mappedMachines = 0;
            foreach (var mapping in MachineMappings.Where(mapping => !mapping.IsResolved))
            {
                if (mapping.AcceptSafeAutomaticMachineSuggestion())
                {
                    mappedMachines++;
                }
            }

            foreach (var row in Rows.Where(IsIncludedInSelectedOutcome))
            {
                if (row.HasExplicitDecision)
                {
                    continue;
                }

                if (row.Kind == "open_orders")
                {
                    row.PrepareOpenOrderAutomatically();
                }
                else if (row.Kind == "planning")
                {
                    row.PreparePlanningAutomatically(BuildAutomaticBatchNumber(row));
                }
            }

            automaticPrepared = true;
            confirmAutomaticSkips = false;
            OnPropertyChanged(nameof(AutomaticPrepared));
            OnPropertyChanged(nameof(ConfirmAutomaticSkips));
            MachineSuggestionSummary = mappedMachines == 0
                ? "No unambiguous exact Machine mapping was applied automatically. Safe Batch rows fall back to Pool."
                : $"Applied {mappedMachines} unambiguous exact Machine mapping(s). Other safe Batch rows fall back to Pool.";

            var attention = AutomaticAttentionRows;
            var hasUnresolvedRows = Rows.Where(IsIncludedInSelectedOutcome).Any(row => !row.IsResolved);
            wizardStep = AutomaticReadyRows > 0 && !hasUnresolvedRows ? 4 : 3;
            selectedWizardRow = attention.FirstOrDefault()
                ?? Rows.FirstOrDefault(row => IsIncludedInSelectedOutcome(row) && row.IsMutation)
                ?? Rows.FirstOrDefault(row => IsIncludedInSelectedOutcome(row));
            Summary = AutomaticImportSummary;
        }
        finally
        {
            isPreparingAutomatically = false;
        }

        foreach (var row in Rows.Where(row => row.Kind == "planning"))
        {
            row.NotifyMachineMappingChanged();
        }

        OnPropertyChanged(nameof(WizardStep));
        OnPropertyChanged(nameof(SelectedWizardRow));
        RaiseState();
        return Task.CompletedTask;
    }

    private string BuildAutomaticBatchNumber(LegacyImportRowViewModel row)
    {
        var hash = preview?.WorkbookSha256?.Trim().ToUpperInvariant() ?? string.Empty;
        var prefix = hash.Length >= 8 ? hash[..8] : hash.PadRight(8, '0');
        return $"IMP-{prefix}-{row.RowNumber.ToString(CultureInfo.InvariantCulture)}";
    }

    private bool IsEligiblePatternTarget(LegacyImportRowViewModel source, LegacyImportRowViewModel target)
    {
        if (ReferenceEquals(source, target) || target.IsSkipped || target.HasExplicitDecision || target.Kind != source.Kind
            || !IsIncludedInSelectedOutcome(source) || !IsIncludedInSelectedOutcome(target))
        {
            return false;
        }

        return PatternScope switch
        {
            "same_machine_section" => source.Kind == "planning"
                && string.Equals(source.SectionKey, target.SectionKey, StringComparison.Ordinal),
            "same_part_and_operation" => target.HasSamePartAndOperationShapeAs(source),
            "all_eligible_rows" => true,
            _ => false
        };
    }

    private void ApplyPreview(LegacyWorkingPlanPreview result)
    {
        isApplyingPreview = true;
        try
        {
            preview = result;
            InvalidateAutomaticDraft();
            expiryTimer.Start();
            SourceSheetName = result.Suggestions.PlanningSheet ?? string.Empty;
            OpenOrdersSheetName = result.Suggestions.OpenOrdersSheet ?? string.Empty;
            HeaderRowNumber = result.MachineSections.Count == 0
                ? 0
                : result.MachineSections.Min(section => section.HeaderRow);

            Mappings.Clear();
            MachineMappings.Clear();
            var planningColumnChoices = ColumnOptionsFor(result, result.Suggestions.PlanningSheet);
            var openOrderColumnChoices = ColumnOptionsFor(result, result.Suggestions.OpenOrdersSheet);
            AddColumnMappings("planning", PlanningColumnTargets,
                result.Suggestions.PlanningColumns ?? [], result.Rows, planningColumnChoices);
            AddColumnMappings("open_orders", OpenOrderColumnTargets,
                result.Suggestions.OpenOrderColumns ?? [], result.OpenOrderRows, openOrderColumnChoices);
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

            hasPendingPreviewCorrections = false;
            hasSheetSelectionCorrections = false;
            hasColumnMappingCorrections = false;
            ResultSummary = string.Empty;
            Summary = $"Server preview ready. {PreviewSummary}";
            WizardStep = 0;
            CurrentImportStage = "cases";
            SelectedWizardRow = Rows.FirstOrDefault();
            PatternApplicationSummary = string.Empty;
            MachineSuggestionSummary = string.Empty;
        }
        finally
        {
            isApplyingPreview = false;
        }

        RaiseState();
    }

    private LegacyWorkingPlanCommit BuildCommit() => new(
        preview!.SchemaVersion,
        preview.ImportToken,
        preview.WorkbookSha256,
        ImportPoolBatches || ImportMachineAssignments ? NullIfBlank(SourceSheetName) : null,
        ImportOrders ? NullIfBlank(OpenOrdersSheetName) : null,
        Mappings.Where(mapping => mapping.Kind == "column"
                && IsIncludedInSelectedOutcome(mapping)
                && mapping.IsResolved
                && !string.IsNullOrWhiteSpace(mapping.SourceColumn))
            .Select(mapping => new LegacyImportColumnMapping(mapping.Scope!, mapping.TargetField, mapping.SourceColumn))
            .ToArray(),
        MachineMappings.Where(mapping => mapping.IsResolved && Rows.Any(row =>
                IsIncludedInSelectedOutcome(row)
                && row.RequiresMachineMapping
                && string.Equals(row.SectionKey, mapping.SectionKey, StringComparison.Ordinal)))
            .Select(mapping => new LegacyImportMachineMapping(mapping.SectionKey!, NullIfBlank(mapping.SelectedMachineId)))
            .ToArray(),
        Rows.Where(row => row.Kind == "open_orders" && IsIncludedInSelectedOutcome(row))
            .Select(row => row.ToOpenOrderSelection()).ToArray(),
        Rows.Where(row => row.Kind == "planning" && IsIncludedInSelectedOutcome(row))
            .Select(row => row.ToPlanningSelection(
                MachineMappings.FirstOrDefault(mapping => mapping.SectionKey == row.SectionKey)?.SelectedMachineId))
            .ToArray());

    private IReadOnlyList<LegacyImportColumnMapping> BuildPreviewColumnMappings() => Mappings
        .Where(mapping => mapping.Kind == "column" && !string.IsNullOrWhiteSpace(mapping.SourceColumn))
        .Select(mapping => new LegacyImportColumnMapping(
            mapping.Scope!,
            mapping.TargetField,
            mapping.SourceColumn))
        .ToArray();

    private string BuildResultSummary(LegacyWorkingPlanCommitReceipt receipt)
    {
        var created = receipt.Created;
        var unchanged = receipt.Unchanged;
        var selectedSkips = Rows.Count(row => IsIncludedInSelectedOutcome(row) && row.IsSkipped);
        var replay = receipt.Replayed ? " (idempotent replay)" : string.Empty;
        return $"Import {receipt.CommitId}{replay}: created {created.CaseIds.Count} Case(s), "
            + $"{created.OrderIds.Count} Order(s), {created.BatchIds.Count} Batch(es), "
            + $"{created.BatchOperationIds?.Count ?? 0} Batch Operation(s), and {created.AssignmentIds.Count} assignment(s); "
            + $"matched/unchanged {unchanged.CaseIds.Count} Case(s), {unchanged.OrderIds.Count} Order(s), "
            + $"{unchanged.BatchIds.Count} Batch(es), {unchanged.BatchOperationIds?.Count ?? 0} Batch Operation(s), "
            + $"and {unchanged.AssignmentIds.Count} assignment(s). {selectedSkips} selected source row(s) skipped; "
            + $"{receipt.PoolBatchOperationIds?.Count ?? 0} Operation(s) left in Pool; "
            + $"{receipt.MachineBacklogs.Count} Machine backlog(s) affected.";
    }

    internal bool IsIncludedInSelectedOutcome(LegacyImportRowViewModel row) => row.Kind switch
    {
        "open_orders" => ImportOrders,
        "planning" => ImportPoolBatches || ImportMachineAssignments,
        _ => false
    };

    private bool IsIncludedInSelectedOutcome(LegacyImportMappingViewModel mapping) => mapping.Scope switch
    {
        "open_orders" => ImportOrders,
        "planning" => ImportPoolBatches || ImportMachineAssignments,
        _ => false
    };

    private bool HasSelectedOutcome => (ImportOrders && !string.IsNullOrWhiteSpace(OpenOrdersSheetName))
        || ((ImportPoolBatches || ImportMachineAssignments) && !string.IsNullOrWhiteSpace(SourceSheetName));

    private bool HasResolvedMappings => Mappings.Where(IsIncludedInSelectedOutcome).All(mapping => mapping.IsResolved)
        && MachineMappings.Where(mapping => Rows.Any(row => row.RequiresMachineMapping
            && row.SectionKey == mapping.SectionKey)).All(mapping => mapping.IsResolved);

    private bool HasResolvedIncludedRows => Rows.Where(IsIncludedInSelectedOutcome)
        .All(row => row.HasExplicitDecision && row.IsResolved)
        && Rows.Any(row => IsIncludedInSelectedOutcome(row) && row.IsMutation && row.IsResolved);

    internal bool IsPlanningActionAvailable(string action)
    {
        // Row-level validation is also used before a preview is attached in focused
        // tests and design-time construction. The wizard outcome gate applies once a
        // Server preview exists.
        if (preview is null)
        {
            return true;
        }

        return action switch
    {
        "create_batch_to_pool" => ImportPoolBatches,
        "create_batch_and_assign" or "assign_existing_operation" => ImportMachineAssignments,
        "skip" => true,
        _ => false
    };
    }

    private IEnumerable<LegacyImportIssue> IssuesFor(string sheetName, int rowNumber) =>
        Issues.Where(issue => string.Equals(issue.SheetName, sheetName, StringComparison.OrdinalIgnoreCase)
            && issue.RowNumber == rowNumber);

    private int CountIssueRows(string severity) => Issues
        .Where(issue => issue.RowNumber.HasValue
            && string.Equals(issue.Severity, severity, StringComparison.OrdinalIgnoreCase))
        .Select(issue => $"{issue.SheetName}:{issue.RowNumber}")
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Count();

    private bool HasGlobalServerBlockers() => Issues.Any(issue => !issue.RowNumber.HasValue
        && IsBlockingSeverity(issue.Severity)
        && (!IsCompatibilityOverrideIssue(issue) || string.IsNullOrWhiteSpace(issue.SectionKey))
        && IsIssueIncludedInSelectedOutcome(issue));

    private bool IsIssueIncludedInSelectedOutcome(LegacyImportIssue issue)
    {
        if (string.Equals(issue.Scope, "planning", StringComparison.OrdinalIgnoreCase))
        {
            return ImportPoolBatches || ImportMachineAssignments;
        }

        if (string.Equals(issue.Scope, "open_orders", StringComparison.OrdinalIgnoreCase))
        {
            return ImportOrders;
        }

        if (!string.IsNullOrWhiteSpace(issue.SheetName))
        {
            var planningSheet = string.Equals(
                issue.SheetName, SourceSheetName, StringComparison.OrdinalIgnoreCase);
            var orderSheet = string.Equals(
                issue.SheetName, OpenOrdersSheetName, StringComparison.OrdinalIgnoreCase);
            if (planningSheet || orderSheet)
            {
                return planningSheet && (ImportPoolBatches || ImportMachineAssignments)
                    || orderSheet && ImportOrders;
            }
        }

        if (!string.IsNullOrWhiteSpace(issue.SectionKey)
            || issue.Code.Contains("machine_section", StringComparison.OrdinalIgnoreCase))
        {
            return ImportPoolBatches || ImportMachineAssignments;
        }

        // Workbook-integrity, token, and otherwise unscoped blockers apply to every
        // outcome and must remain visible.
        return true;
    }

    private bool HasUniqueColumnTargetFields() => Mappings
        .GroupBy(mapping => $"{mapping.Scope}:{mapping.TargetField}", StringComparer.OrdinalIgnoreCase)
        .All(group => group.Count() == 1);

    private bool CanPreview() => apiClient is not null && !IsBusy
        && workbookExists(SelectedFilePath)
        && SelectedFilePath.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase);

    private bool CanSkipRow(LegacyImportRowViewModel? row) => !IsBusy && IsEditor && row is not null;

    private void MarkPreviewCorrectionPending()
    {
        if (preview is null || isApplyingPreview)
        {
            RaiseState();
            return;
        }

        InvalidateAutomaticDraft();
        if (!hasPendingPreviewCorrections)
        {
            hasPendingPreviewCorrections = true;
            ResultSummary = string.Empty;
            Summary = "Sheet or column choices changed. Validate / refresh the preview before resolving rows or committing.";
        }

        RaiseState();
    }

    private void InvalidateAutomaticDraft()
    {
        automaticPrepared = false;
        confirmAutomaticSkips = false;
        OnPropertyChanged(nameof(AutomaticPrepared));
        OnPropertyChanged(nameof(ConfirmAutomaticSkips));
        OnPropertyChanged(nameof(RequiresAutomaticSkipConfirmation));
        OnPropertyChanged(nameof(AutomaticImportSummary));
    }

    private void ClearPreview()
    {
        preview = null;
        expiryTimer.Stop();
        Mappings.Clear();
        MachineMappings.Clear();
        Rows.Clear();
        Issues.Clear();
        HeaderRowNumber = 0;
        sourceSheetName = string.Empty;
        openOrdersSheetName = string.Empty;
        hasPendingPreviewCorrections = false;
        hasSheetSelectionCorrections = false;
        hasColumnMappingCorrections = false;
        ResultSummary = string.Empty;
        WizardStep = 0;
        SelectedWizardRow = null;
        PatternApplicationSummary = string.Empty;
        MachineSuggestionSummary = string.Empty;
        automaticPrepared = false;
        confirmAutomaticSkips = false;
        hasExplicitOutcomeSelection = false;
        importOrders = false;
        importPoolBatches = false;
        importMachineAssignments = false;
        if (!IsBusy)
        {
            Summary = "Choose an .xlsx workbook to create a Server preview.";
        }

        OnPropertyChanged(nameof(SourceSheetName));
        OnPropertyChanged(nameof(OpenOrdersSheetName));
        OnPropertyChanged(nameof(DetectedSheets));
        OnPropertyChanged(nameof(SheetChoices));
        OnPropertyChanged(nameof(OptionalSheetChoices));
        OnPropertyChanged(nameof(AutomaticPrepared));
        OnPropertyChanged(nameof(ConfirmAutomaticSkips));
        OnPropertyChanged(nameof(ImportOrders));
        OnPropertyChanged(nameof(ImportPoolBatches));
        OnPropertyChanged(nameof(ImportMachineAssignments));
        OnPropertyChanged(nameof(RequiresAutomaticSkipConfirmation));
        OnPropertyChanged(nameof(AutomaticImportSummary));
        OnPropertyChanged(nameof(AutomaticReadyRows));
        OnPropertyChanged(nameof(AutomaticSkippedRows));
        OnPropertyChanged(nameof(AutomaticAttentionRows));
    }

    internal void RowOrMappingChanged()
    {
        if (isPreparingAutomatically)
        {
            return;
        }

        ResetAutomaticSkipConfirmation();
        foreach (var row in Rows.Where(row => row.Kind == "planning"))
        {
            row.NotifyMachineMappingChanged();
        }

        RaiseState();
    }

    internal void ColumnMappingChanged()
    {
        ResetAutomaticSkipConfirmation();
        if (preview is not null && !isApplyingPreview) hasColumnMappingCorrections = true;
        MarkPreviewCorrectionPending();
    }

    internal LegacyImportMachineCandidate? SelectedMachineForSection(string? sectionKey) =>
        MachineMappings.FirstOrDefault(mapping => string.Equals(
            mapping.SectionKey, sectionKey, StringComparison.Ordinal))?.SelectedMachineCandidate;

    internal bool HasSafeAutomaticMachineSelection(string? sectionKey)
    {
        var mapping = MachineMappings.FirstOrDefault(candidate => string.Equals(
            candidate.SectionKey, sectionKey, StringComparison.Ordinal));
        if (mapping is null || !mapping.HasSafeAutomaticMachineSuggestion
            || mapping.SelectedMachineCandidate is null)
        {
            return false;
        }

        var best = mapping.MachineChoices.OrderByDescending(candidate => candidate.Score).First();
        return string.Equals(
            mapping.SelectedMachineCandidate.MachineId,
            best.MachineId,
            StringComparison.Ordinal);
    }

    internal bool ServerRequiresCompatibilityOverride(string sheetName, int rowNumber, string? sectionKey) =>
        Issues.Any(issue => IsCompatibilityOverrideIssue(issue)
            && ((string.Equals(issue.SheetName, sheetName, StringComparison.OrdinalIgnoreCase)
                    && issue.RowNumber == rowNumber)
                || (!issue.RowNumber.HasValue
                    && string.Equals(issue.SectionKey, sectionKey, StringComparison.Ordinal))));

    private void RaiseState()
    {
        OnPropertyChanged(nameof(CanCommit));
        OnPropertyChanged(nameof(ExpiresAt));
        OnPropertyChanged(nameof(TokenExpiryText));
        OnPropertyChanged(nameof(DetectedSheets));
        OnPropertyChanged(nameof(SheetChoices));
        OnPropertyChanged(nameof(OptionalSheetChoices));
        OnPropertyChanged(nameof(PreviewSummary));
        OnPropertyChanged(nameof(PreviewCorrectionStatus));
        OnPropertyChanged(nameof(PreviewActionText));
        OnPropertyChanged(nameof(HasPendingPreviewCorrections));
        PreviewCommand.RaiseCanExecuteChanged();
        CommitCommand.RaiseCanExecuteChanged();
        SkipRowCommand.RaiseCanExecuteChanged();
        SkipAllUnresolvedCommand.RaiseCanExecuteChanged();
        PrepareAutomaticallyCommand.RaiseCanExecuteChanged();
        RaiseWizardState();
    }

    private void RaiseWizardState()
    {
        OnPropertyChanged(nameof(WizardStepTitle));
        OnPropertyChanged(nameof(IsSourceMappingStep));
        OnPropertyChanged(nameof(IsPreviewStep));
        OnPropertyChanged(nameof(IsOutcomesStep));
        OnPropertyChanged(nameof(IsResolutionStep));
        OnPropertyChanged(nameof(IsOrdersStep));
        OnPropertyChanged(nameof(IsPoolStep));
        OnPropertyChanged(nameof(IsAssignmentStep));
        OnPropertyChanged(nameof(IsReviewStep));
        OnPropertyChanged(nameof(CanCommitNow));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanGoPrevious));
        OnPropertyChanged(nameof(CanApplyPattern));
        OnPropertyChanged(nameof(PatternPreviewText));
        OnPropertyChanged(nameof(ValidationSummary));
        OnPropertyChanged(nameof(PreviewSummary));
        OnPropertyChanged(nameof(SelectedRow));
        OnPropertyChanged(nameof(PoolRows));
        OnPropertyChanged(nameof(OrderRows));
        OnPropertyChanged(nameof(BatchRows));
        OnPropertyChanged(nameof(AssignmentRows));
        OnPropertyChanged(nameof(CaseRows));
        OnPropertyChanged(nameof(CurrentStageRows));
        OnPropertyChanged(nameof(CurrentImportStageTitle));
        OnPropertyChanged(nameof(CurrentImportStageDescription));
        OnPropertyChanged(nameof(IncludedRows));
        OnPropertyChanged(nameof(ReviewRows));
        OnPropertyChanged(nameof(IncludedMappings));
        OnPropertyChanged(nameof(IncludedMachineMappings));
        OnPropertyChanged(nameof(ShowsMachineMappings));
        OnPropertyChanged(nameof(AutomaticPrepared));
        OnPropertyChanged(nameof(AutomaticImportSummary));
        OnPropertyChanged(nameof(AutomaticReadyRows));
        OnPropertyChanged(nameof(AutomaticSkippedRows));
        OnPropertyChanged(nameof(AutomaticAttentionRows));
        OnPropertyChanged(nameof(RequiresAutomaticSkipConfirmation));
        OnPropertyChanged(nameof(ConfirmAutomaticSkips));
        NextStepCommand.RaiseCanExecuteChanged();
        PreviousStepCommand.RaiseCanExecuteChanged();
        ApplyPatternCommand.RaiseCanExecuteChanged();
        ApplySelectedPatternToSimilarCommand.RaiseCanExecuteChanged();
        ApplySelectedPatternToAllCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanAcceptClearMachineSuggestions));
        AcceptClearMachineSuggestionsCommand.RaiseCanExecuteChanged();
        ShowCasesStageCommand.RaiseCanExecuteChanged();
        ShowOrdersStageCommand.RaiseCanExecuteChanged();
        ShowBatchesStageCommand.RaiseCanExecuteChanged();
        ShowAssignmentsStageCommand.RaiseCanExecuteChanged();
    }

    private Task ShowImportStageAsync(string stage)
    {
        if (preview is null || IsBusy)
        {
            return Task.CompletedTask;
        }

        CurrentImportStage = stage;
        switch (stage)
        {
            case "cases":
            case "orders":
                ImportOrders = true;
                break;
            case "batches":
                ImportPoolBatches = true;
                break;
            case "assignments":
                ImportMachineAssignments = true;
                break;
        }
        WizardStep = 3;
        SelectedWizardRow = CurrentStageRows.FirstOrDefault();
        RaiseWizardState();
        return Task.CompletedTask;
    }

    internal void SynchronizeCaseStageSelection(LegacyImportRowViewModel source)
    {
        if (isSynchronizingCaseStageSelection
            || CurrentImportStage != "cases"
            || source.Kind != "open_orders")
        {
            return;
        }

        var partNumber = source.SourcePartNumber?.Trim();
        if (string.IsNullOrWhiteSpace(partNumber))
        {
            return;
        }

        isSynchronizingCaseStageSelection = true;
        try
        {
            foreach (var related in OrderRows.Where(row => !ReferenceEquals(row, source)
                && string.Equals(row.SourcePartNumber?.Trim(), partNumber, StringComparison.OrdinalIgnoreCase)))
            {
                if (source.SelectedCaseCandidate is not null
                    && related.CaseCandidates.Contains(source.SelectedCaseCandidate))
                {
                    related.SelectedCaseCandidate = source.SelectedCaseCandidate;
                }

                related.CaseSourceRowKey = string.Equals(source.Decision, "create_case", StringComparison.Ordinal)
                    ? source.RowKey
                    : string.Empty;
            }
        }
        finally
        {
            isSynchronizingCaseStageSelection = false;
        }

        RaiseWizardState();
    }

    private string PatternScopeDescription => PatternScope switch
    {
        "same_machine_section" => "unresolved rows in the same Machine section",
        "same_part_and_operation" => "unresolved rows with the same Case/Part and operation shape",
        "all_eligible_rows" => "all unresolved rows of the same import kind",
        _ => "no rows"
    };

    private void SetOutcome(ref bool field, bool value, [CallerMemberName] string? name = null)
    {
        if (!SetField(ref field, value, name))
        {
            return;
        }

        hasExplicitOutcomeSelection = true;
        foreach (var row in Rows)
        {
            row.NotifyOutcomeSelectionChanged();
        }

        ResetAutomaticSkipConfirmation();
        RaiseState();
    }

    private void ResetAutomaticSkipConfirmation()
    {
        if (!isPreparingAutomatically && confirmAutomaticSkips)
        {
            confirmAutomaticSkips = false;
            OnPropertyChanged(nameof(ConfirmAutomaticSkips));
        }
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

    private void AddColumnMappings<T>(
        string scope,
        IReadOnlyList<(string Field, bool Required)> knownTargets,
        IReadOnlyList<LegacyImportColumnSuggestion> suggestions,
        IReadOnlyList<T>? rows,
        IReadOnlyList<LegacyImportSourceColumnChoice> columnChoices) where T : class
    {
        var suggestionByField = suggestions
            .Where(suggestion => !string.IsNullOrWhiteSpace(suggestion.Field))
            .GroupBy(suggestion => suggestion.Field, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var target in knownTargets)
        {
            var suggestion = suggestionByField.GetValueOrDefault(target.Field)
                ?? new LegacyImportColumnSuggestion(
                    target.Field,
                    Column: null,
                    Header: "Not detected - choose a source column",
                    Confidence: 0m,
                    Required: target.Required);
            Mappings.Add(LegacyImportMappingViewModel.Column(
                scope, suggestion, this, SampleFor(rows, target.Field), columnChoices));
        }

        // Keep the client forward-compatible if a newer Server adds a target field.
        foreach (var suggestion in suggestions.Where(suggestion =>
                     knownTargets.All(target => !string.Equals(
                         target.Field, suggestion.Field, StringComparison.OrdinalIgnoreCase))))
        {
            Mappings.Add(LegacyImportMappingViewModel.Column(
                scope, suggestion, this, SampleFor(rows, suggestion.Field), columnChoices));
        }
    }

    private static IReadOnlyList<LegacyImportSourceColumnChoice> ColumnOptionsFor(
        LegacyWorkingPlanPreview preview,
        string? sheetName)
    {
        var sheet = preview.Workbook.Sheets
            .FirstOrDefault(candidate => string.Equals(candidate.Name, sheetName, StringComparison.OrdinalIgnoreCase));
        var count = sheet?.ColumnCount ?? 0;
        var descriptors = (sheet?.Columns ?? [])
            .Where(column => !string.IsNullOrWhiteSpace(column.Column))
            .GroupBy(column => column.Column.Trim().ToUpperInvariant(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        return Enumerable.Range(1, Math.Min(count, 16_384))
            .Select(ToExcelColumnName)
            .Select(column => descriptors.TryGetValue(column, out var descriptor)
                ? new LegacyImportSourceColumnChoice(column, descriptor.Header, descriptor.Sample)
                : new LegacyImportSourceColumnChoice(column, null, null))
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

    internal static bool IsCompatibilityOverrideIssue(LegacyImportIssue issue) =>
        string.Equals(issue.Code, "machine_type_override_required", StringComparison.OrdinalIgnoreCase)
        || string.Equals(issue.Code, "machine_type_mismatch", StringComparison.OrdinalIgnoreCase);

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

internal sealed record LegacyImportChoice(string Value, string DisplayName);

internal sealed record LegacyImportSourceColumnChoice(string Column, string? Header, string? Sample)
{
    public string DisplayName => string.Join(" - ", new[] { Column, Header, Sample }
        .Where(value => !string.IsNullOrWhiteSpace(value)));
}

internal sealed class LegacyImportMappingViewModel : INotifyPropertyChanged
{
    private readonly LegacyExcelImportViewModel owner;
    private string targetField;
    private string sourceColumn = string.Empty;
    private readonly string suggestedSourceHeader;
    private readonly string suggestedSampleValue;
    private string selectedMachineId = string.Empty;
    private LegacyImportMachineCandidate? selectedMachineCandidate;

    private LegacyImportMappingViewModel(LegacyExcelImportViewModel owner, string kind, string sourceHeader,
        string sampleValue, string targetField, bool required, decimal? candidateScore, string candidateReason)
    {
        this.owner = owner;
        Kind = kind;
        suggestedSourceHeader = sourceHeader;
        suggestedSampleValue = sampleValue;
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
            if (SetField(ref sourceColumn, normalized))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SourceHeader)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SampleValue)));
                owner.ColumnMappingChanged();
            }
        }
    }
    public string SourceHeader => SelectedColumnOption?.Header ?? suggestedSourceHeader;
    public string SampleValue => SelectedColumnOption?.Sample ?? suggestedSampleValue;
    public bool IsRequired { get; }
    public decimal? CandidateScore { get; }
    public string CandidateReason { get; }
    public string SelectionReason => Kind == "machine" && SelectedMachineCandidate is not null
        ? SelectedMachineCandidate.Reason
        : CandidateReason;
    public IReadOnlyList<LegacyImportSourceColumnChoice> ColumnOptions { get; private init; } = [];
    public IReadOnlyList<string> ColumnChoices => ColumnOptions.Select(option => option.Column).ToArray();
    public IReadOnlyList<LegacyImportMachineCandidate> MachineChoices { get; private init; } = [];
    public string TargetField => targetField;
    private LegacyImportSourceColumnChoice? SelectedColumnOption => ColumnOptions.FirstOrDefault(option =>
        string.Equals(option.Column, SourceColumn, StringComparison.OrdinalIgnoreCase));
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
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectionReason)));
                SelectedMachineId = value?.MachineId ?? string.Empty;
            }
        }
    }
    public string Decision => IsResolved
        ? string.IsNullOrWhiteSpace(SourceColumn) && Kind == "column" ? "Optional" : "Selected"
        : "Choose";
    public bool IsResolved => Kind == "column"
        ? !string.IsNullOrWhiteSpace(TargetField)
            && (string.IsNullOrWhiteSpace(SourceColumn)
                ? !IsRequired
                : ColumnChoices.Contains(SourceColumn, StringComparer.OrdinalIgnoreCase))
        : !string.IsNullOrWhiteSpace(SelectedMachineId);

    public bool HasSourceMachineMatch => Kind != "machine"
        || MachineChoices.Any(candidate => candidate.Score > 0m);

    internal bool HasClearMachineSuggestion
    {
        get
        {
            if (Kind != "machine" || MachineChoices.Count == 0)
            {
                return false;
            }

            var ordered = MachineChoices.OrderByDescending(candidate => candidate.Score).ToArray();
            return ordered[0].Score >= 0.80m
                && (ordered.Length == 1 || ordered[0].Score - ordered[1].Score >= 0.15m);
        }
    }

    internal bool HasSafeAutomaticMachineSuggestion
    {
        get
        {
            if (Kind != "machine" || MachineChoices.Count == 0)
            {
                return false;
            }

            var ordered = MachineChoices.OrderByDescending(candidate => candidate.Score).ToArray();
            return ordered[0].Score >= 0.95m
                && (ordered.Length == 1 || ordered[0].Score - ordered[1].Score >= 0.15m);
        }
    }

    internal bool AcceptClearMachineSuggestion()
    {
        if (IsResolved || !HasClearMachineSuggestion)
        {
            return false;
        }

        SelectedMachineCandidate = MachineChoices.OrderByDescending(candidate => candidate.Score).First();
        return true;
    }

    internal bool AcceptSafeAutomaticMachineSuggestion()
    {
        if (IsResolved || !HasSafeAutomaticMachineSuggestion)
        {
            return false;
        }

        SelectedMachineCandidate = MachineChoices.OrderByDescending(candidate => candidate.Score).First();
        return true;
    }

    internal static LegacyImportMappingViewModel Column(string scope, LegacyImportColumnSuggestion suggestion,
        LegacyExcelImportViewModel owner, string sample, IReadOnlyList<LegacyImportSourceColumnChoice> columnChoices) => new(owner, "column", suggestion.Header ?? suggestion.Column ?? "(unlabeled)",
            sample, suggestion.Field, suggestion.Required ?? IsRequiredColumn(scope, suggestion.Field), suggestion.Confidence,
            string.IsNullOrWhiteSpace(suggestion.Column)
                ? "No automatic match; choose a source column"
                : "Server suggestion")
        {
            Scope = scope,
            sourceColumn = suggestion.Column?.Trim().ToUpperInvariant() ?? string.Empty,
            ColumnOptions = columnChoices
        };

    internal static LegacyImportMappingViewModel Machine(LegacyImportMachineSection section,
        LegacyExcelImportViewModel owner) => new(owner, "machine", section.SourceLabel, string.Empty, string.Empty,
            required: true, section.Candidates.FirstOrDefault()?.Score, section.Candidates.FirstOrDefault()?.Reason ?? "Choose an existing Machine")
        {
            SectionKey = section.SectionKey,
            MachineChoices = section.Candidates ?? [],
        };

    private static bool IsRequiredColumn(string scope, string field) => scope switch
    {
        // These identify a production planning row and its conservative Batch quantity.
        "planning" => field is "partNumber" or "quantity",
        // An open-order row may be corrected manually later, but Part Number is the
        // stable Case matching key used by the staged preview.
        "open_orders" => field == "partNumber",
        _ => false
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
    private string automaticReason = string.Empty;
    private bool includeOrderWithNewCase;
    private bool orderFieldsEdited;
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
    private IReadOnlyList<LegacyImportCaseOperationCandidate> AllRouteOperationCandidates { get; init; } = [];
    public IReadOnlyList<LegacyImportCaseOperationCandidate> RouteOperationCandidates => selectedCaseCandidate is null
        ? []
        : AllRouteOperationCandidates.Where(candidate => string.Equals(
            candidate.CaseId, selectedCaseCandidate.CaseId, StringComparison.Ordinal)).ToArray();
    public int SelectedCaseRouteOperationCount => RouteOperationCandidates.Count;
    public IReadOnlyList<LegacyImportCaseOperationCandidate> AvailableRouteOperationCandidates => RouteOperationCandidates;
    public IReadOnlyList<LegacyImportBatchOperationCandidate> ExistingOperationCandidates { get; private init; } = [];
    public IReadOnlyList<LegacyImportBatchOperationCandidate> AvailableExistingOperationCandidates =>
        ExistingOperationCandidates.Where(candidate => !candidate.IsAlreadyAssigned).ToArray();
    public ObservableCollection<LegacyImportAllocationViewModel> Allocations { get; } = [];
    public AsyncCommand AddAllocationCommand { get; }
    public AsyncCommand<LegacyImportAllocationViewModel> RemoveAllocationCommand { get; }
    public IReadOnlyList<string> SkipChoices { get; } = ["Skip this source row"];
    public string AutomaticReason
    {
        get => automaticReason;
        private set
        {
            if (SetField(ref automaticReason, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Message)));
            }
        }
    }
    public string Message => string.Join(" ", new[]
        {
            string.IsNullOrWhiteSpace(AutomaticReason) ? null : AutomaticReason,
            issues.Count == 0 ? null : string.Join(" ", issues.Select(issue => issue.Message))
        }.Where(value => !string.IsNullOrWhiteSpace(value))) is { Length: > 0 } message
            ? message
            : "No Server issue reported.";
    public string Status => IsSkipped ? "Skip"
        : HasUnresolvedBlockingIssue || RequiresCompatibilityOverride && !HasValidCompatibilityOverride() ? "Blocked"
        : issues.Any(issue => string.Equals(issue.Severity, "warning", StringComparison.OrdinalIgnoreCase))
            || RequiresCompatibilityOverride ? "Warning"
        : IsResolved ? "Ready" : "Needs review";
    public bool HasExplicitDecision => !string.IsNullOrWhiteSpace(Decision);
    public string DecisionDisplayName => Decision switch
    {
        "skip" => "Skip",
        "create_case" => IncludeOrderWithNewCase ? "Create Case and Order" : "Create Case",
        "create_order" => "Create Order under selected Case",
        "create_batch_to_pool" => "Create full-route Batch in Pool",
        "create_batch_and_assign" => "Create Batch and assign selected operation",
        "assign_existing_operation" => "Assign existing Batch operation",
        _ => "Needs decision"
    };
    public bool IsMutation => HasExplicitDecision && !IsSkipped;
    public bool IsResolved => IsSkipped || (!HasUnresolvedBlockingIssue && HasCompleteDecision());
    public IReadOnlyList<LegacyImportChoice> ActionChoices => Kind == "planning"
        ? new LegacyImportChoice[]
            {
                new LegacyImportChoice("skip", "Skip this source row"),
                new LegacyImportChoice("create_batch_to_pool", "Create Batch in unassigned pool"),
                new LegacyImportChoice("create_batch_and_assign", "Create Batch and assign selected operation"),
                new LegacyImportChoice("assign_existing_operation", "Assign existing Batch operation")
            }.Where(choice => owner.IsPlanningActionAvailable(choice.Value)).ToArray()
        : new LegacyImportChoice[]
        {
            new LegacyImportChoice("skip", "Skip this source row"),
            new LegacyImportChoice("create_case", "Create Case (and optional Order)"),
            new LegacyImportChoice("create_order", "Create Order under selected Case")
        };
    public LegacyImportMachineCandidate? SelectedMachineCandidate => owner.SelectedMachineForSection(SectionKey);
    public bool RequiresMachineMapping => Kind == "planning"
        && !IsSkipped
        && Decision is "assign_existing_operation" or "create_batch_and_assign";
    public bool ShowsExistingOperation => Decision == "assign_existing_operation";
    public bool ShowsCaseCandidate => Decision is "create_batch_to_pool" or "create_batch_and_assign" or "create_order";
    public bool ShowsBatchInputs => Decision is "create_batch_to_pool" or "create_batch_and_assign";
    public bool ShowsRouteOperation => Decision == "create_batch_and_assign";
    public bool ShowsAllocations => ShowsBatchInputs;
    public bool ShowsNewCaseInputs => Kind == "open_orders" && Decision == "create_case";
    public bool ShowsOrderInputs => Kind == "open_orders" && Decision is "create_case" or "create_order";
    public bool ShowsCompatibilityReview => RequiresCompatibilityOverride || RequiresMachineMapping;
    public string? SelectedRequiredMachineType => Decision == "assign_existing_operation"
        ? SelectedExistingOperationCandidate?.RequiredMachineType
        : Decision == "create_batch_and_assign"
            ? SelectedRouteOperationCandidate?.RequiredMachineType
            : null;
    public bool RequiresCompatibilityOverride => Kind == "planning"
        && HasSelectedPlanningAction
        && (owner.ServerRequiresCompatibilityOverride(SheetName, RowNumber, SectionKey)
            || IsLocallyKnownIncompatible);
    public string CompatibilityReviewText
    {
        get
        {
            var required = SelectedRequiredMachineType ?? "the selected Operation requirement";
            var machine = SelectedMachineCandidate is null
                ? "no selected Machine"
                : $"{SelectedMachineCandidate.Number} ({SelectedMachineCandidate.ProcessType}"
                    + (string.IsNullOrWhiteSpace(SelectedMachineCandidate.AxisType)
                        ? ")"
                        : $", {SelectedMachineCandidate.AxisType})");
            if (!HasSelectedPlanningAction)
            {
                return "Choose a planning action to review its Machine requirement.";
            }

            if (SelectedMachineCandidate is null)
            {
                return $"Choose a Machine mapping to validate {required}.";
            }

            if (owner.ServerRequiresCompatibilityOverride(SheetName, RowNumber, SectionKey))
            {
                return $"Server reported an incompatibility for {required} on {machine}. Confirm the override and enter a reason.";
            }

            var matchSource = CompatibilityMatchSource;
            if (matchSource is not null)
            {
                return $"{machine} matches {required} through its {matchSource}. The Server will validate it again on commit.";
            }

            return HasCompleteCompatibilityFacts
                ? $"{machine} does not match {required} through its process, axis, or declared capabilities. Confirm the override and enter a reason."
                : $"Server will validate {required} against {machine}, including Machine-Type capabilities.";
        }
    }
    public string Decision { get => decision; set => SetDecision(value); }
    public bool IsSkipped { get => isSkipped; set { if (SetField(ref isSkipped, value)) SetDecision(value ? "skip" : string.Empty); } }
    public bool CreateBatch { get => createBatch; set { if (SetField(ref createBatch, value)) SetDecision(value ? "create_batch_to_pool" : string.Empty); } }
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
            if (!SetField(ref selectedCaseCandidate, value)) return;

            CaseId = value?.CaseId ?? string.Empty;
            ExistingCaseId = value?.CaseId ?? string.Empty;
            if (selectedRouteOperationCandidate is not null
                && (value is null || !string.Equals(selectedRouteOperationCandidate.CaseId, value.CaseId, StringComparison.Ordinal)))
            {
                selectedRouteOperationCandidate = null;
                routeOperation = string.Empty;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedRouteOperationCandidate)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RouteOperation)));
            }

            RaiseCaseSelectionProperties();
            owner.SynchronizeCaseStageSelection(this);
        }
    }
    public LegacyImportCaseOperationCandidate? SelectedRouteOperationCandidate
    {
        get => selectedRouteOperationCandidate;
        set
        {
            if (value is not null && !RouteOperationCandidates.Contains(value)) return;
            if (SetField(ref selectedRouteOperationCandidate, value))
            {
                RouteOperation = value?.CaseOperationId ?? string.Empty;
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
    public string OrderNumber { get => orderNumber; set => SetOrderField(ref orderNumber, value); }
    public string OrderQuantity { get => orderQuantity; set => SetOrderField(ref orderQuantity, value); }
    public string OrderWorkFinishDate { get => orderWorkFinishDate; set => SetOrderField(ref orderWorkFinishDate, value); }
    public string OrderNotes { get => orderNotes; set => SetOrderField(ref orderNotes, value); }
    public bool IncludeOrderWithNewCase { get => includeOrderWithNewCase; set => SetField(ref includeOrderWithNewCase, value); }

    internal static LegacyImportRowViewModel Planning(LegacyImportPlanningRow row,
        IEnumerable<LegacyImportIssue> issues, LegacyExcelImportViewModel owner) => new(owner, "planning", row.RowKey, row.SheetName, row.RowNumber, row.SectionKey, issues.ToArray())
        {
            CaseCandidates = row.Candidates?.Cases ?? [],
            OrderCandidates = row.Candidates?.Orders ?? [],
            BatchCandidates = row.Candidates?.Batches ?? [],
            AllRouteOperationCandidates = row.Candidates?.CaseOperations ?? [],
            ExistingOperationCandidates = EnrichBatchOperationCandidates(row.Candidates),
            SourcePartNumber = row.Values?.PartNumber,
            SourceQuantity = row.Values?.Quantity,
            SourceCustomer = row.Values?.Customer,
            SourceReferenceOrOrderNumber = row.Values?.CaseReference
        };

    private static IReadOnlyList<LegacyImportBatchOperationCandidate> EnrichBatchOperationCandidates(
        LegacyImportPlanningCandidates? candidates)
    {
        var batchNumbers = (candidates?.Batches ?? [])
            .GroupBy(batch => batch.BatchId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().BatchNumber, StringComparer.Ordinal);
        return (candidates?.BatchOperations ?? [])
            .Select(operation => operation with
            {
                BatchNumber = batchNumbers.TryGetValue(operation.BatchId, out var number) ? number : operation.BatchNumber
            })
            .ToArray();
    }

    internal static LegacyImportRowViewModel OpenOrder(LegacyImportOpenOrderRow row,
        IEnumerable<LegacyImportIssue> issues, LegacyExcelImportViewModel owner)
    {
        var values = row.Values;
        var model = new LegacyImportRowViewModel(owner, "open_orders", row.RowKey, row.SheetName, row.RowNumber, null, issues.ToArray())
        {
            CaseCandidates = row.Candidates?.Cases ?? [],
            OrderCandidates = row.Candidates?.Orders ?? [],
            SourcePartNumber = values?.PartNumber,
            SourceQuantity = values?.OutstandingQuantity ?? values?.OrderedQuantity,
            SourceCustomer = values?.Customer,
            SourceReferenceOrOrderNumber = values?.OrderNumber ?? values?.CaseReference,
            newCasePartNumber = values?.PartNumber ?? string.Empty,
            newCaseName = values?.ItemName ?? values?.PartNumber ?? string.Empty,
            newCaseRevision = values?.Revision ?? string.Empty,
            newCaseCustomer = values?.Customer ?? string.Empty,
            newCaseCustomerReference = values?.CaseReference ?? string.Empty,
            newCaseNotes = values?.Notes ?? string.Empty,
            orderNumber = values?.OrderNumber ?? string.Empty,
            orderQuantity = (values?.OutstandingQuantity ?? values?.OrderedQuantity)?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            orderWorkFinishDate = NormalizeDate(values?.DeliveryDate),
            orderNotes = values?.Notes ?? string.Empty
        };
        return model;
    }

    internal void PrepareOpenOrderAutomatically()
    {
        if (HasAutomaticBlockingIssue())
        {
            SkipAutomatically("Skipped automatically because the Server reported a blocking source-row issue.");
            return;
        }

        if (HasDuplicateSourceWarning())
        {
            SkipAutomatically("Skipped automatically because this is a duplicate source row.");
            return;
        }

        if (OrderCandidates.Count > 0)
        {
            SkipAutomatically(
                $"Skipped automatically because existing Order {OrderCandidates[0].OrderNumber} already matches this Case and Order Number.");
            return;
        }

        if (CaseCandidates.Count != 1)
        {
            SkipAutomatically(CaseCandidates.Count == 0
                ? "Skipped automatically because no existing Case matches the Part Number; the importer never invents a Case folder."
                : "Skipped automatically because more than one existing Case matches the Part Number.");
            return;
        }

        if (!HasCompleteOrderInput)
        {
            SkipAutomatically("Skipped automatically because Order Number, positive quantity, and Work Finish Date are not all valid.");
            return;
        }

        SelectedCaseCandidate = CaseCandidates[0];
        Decision = "create_order";
        AutomaticReason = "Prepared automatically under the one exact existing Case match.";
    }

    internal void PreparePlanningAutomatically(string automaticBatchNumber)
    {
        if (HasAutomaticBlockingIssue())
        {
            SkipAutomatically("Skipped automatically because the Server reported a blocking source-row issue.");
            return;
        }

        if (HasDuplicateSourceWarning())
        {
            SkipAutomatically("Skipped automatically because this is a duplicate source row.");
            return;
        }

        if (SourceQuantity is not > 0)
        {
            SkipAutomatically("Skipped automatically because the planning quantity is not a positive whole number.");
            return;
        }

        if (CaseCandidates.Count != 1)
        {
            SkipAutomatically(CaseCandidates.Count == 0
                ? "Skipped automatically because no existing routed Case matches the Part Number."
                : "Skipped automatically because more than one existing Case matches the Part Number.");
            return;
        }

        SelectedCaseCandidate = CaseCandidates[0];
        if (!HasCompleteReviewedCaseRoute())
        {
            SkipAutomatically("Skipped automatically because the exact Case has no complete versioned Operation route to snapshot.");
            return;
        }

        if (BatchCandidates.Any(candidate => string.Equals(
                candidate.BatchNumber, automaticBatchNumber, StringComparison.OrdinalIgnoreCase)))
        {
            SkipAutomatically($"Skipped automatically because Batch Number {automaticBatchNumber} already exists for this Case.");
            return;
        }

        BatchNumber = automaticBatchNumber;
        var allocation = new LegacyImportAllocationViewModel(OrderCandidates, () =>
        {
            owner.RowOrMappingChanged();
            RaiseStateProperties();
        })
        {
            Type = "stock",
            Quantity = SourceQuantity.Value.ToString(CultureInfo.InvariantCulture)
        };
        Allocations.Add(allocation);

        var machine = SelectedMachineCandidate;
        var compatibleRoute = owner.HasSafeAutomaticMachineSelection(SectionKey)
            && !owner.ServerRequiresCompatibilityOverride(SheetName, RowNumber, SectionKey)
            && machine is not null
            ? RouteOperationCandidates.Where(operation => IsCompatible(machine, operation.RequiredMachineType)).ToArray()
            : [];
        if (compatibleRoute.Length == 1)
        {
            SelectedRouteOperationCandidate = compatibleRoute[0];
            Decision = "create_batch_and_assign";
            AutomaticReason = "Prepared automatically with the exact Machine-section match and its one compatible route Operation.";
            return;
        }

        Decision = "create_batch_to_pool";
        AutomaticReason = machine is null
            ? "Prepared automatically in Pool because no unambiguous exact Machine mapping was available."
            : compatibleRoute.Length == 0
                ? "Prepared automatically in Pool because no route Operation was safely compatible with the exact Machine."
                : "Prepared automatically in Pool because more than one route Operation was compatible with the exact Machine.";
    }

    private void SkipAutomatically(string reason)
    {
        SkipReason = reason;
        Decision = "skip";
        AutomaticReason = reason;
    }

    private bool HasAutomaticBlockingIssue() => issues.Any(issue =>
        string.Equals(issue.Severity, "blocking", StringComparison.OrdinalIgnoreCase)
        && !LegacyExcelImportViewModel.IsCompatibilityOverrideIssue(issue));

    private bool HasDuplicateSourceWarning() => issues.Any(issue =>
        string.Equals(issue.Code, "duplicate_source_row", StringComparison.OrdinalIgnoreCase));

    private static bool IsCompatible(
        LegacyImportMachineCandidate machine,
        string? requiredMachineType)
    {
        var required = requiredMachineType?.Trim();
        return string.IsNullOrWhiteSpace(required)
            || string.Equals(machine.ProcessType, required, StringComparison.OrdinalIgnoreCase)
            || string.Equals(machine.AxisType, required, StringComparison.OrdinalIgnoreCase)
            || (machine.Capabilities ?? []).Contains(required, StringComparer.OrdinalIgnoreCase)
            || (machine.MachineTypeCapabilities ?? []).Contains(required, StringComparer.OrdinalIgnoreCase);
    }

    internal LegacyImportOpenOrderSelection ToOpenOrderSelection() => Decision switch
    {
        "skip" => new LegacyImportOpenOrderSelection(RowKey, "skip", null, null, null),
        "create_case" => new LegacyImportOpenOrderSelection(RowKey, "create_case", null, BuildNewCase(), BuildOrder()),
        "create_order" => new LegacyImportOpenOrderSelection(
            RowKey, "create_order", NullIfBlank(ExistingCaseId), null, BuildOrder(), NullIfBlank(CaseSourceRowKey)),
        _ => new LegacyImportOpenOrderSelection(RowKey, Decision, NullIfBlank(ExistingCaseId), BuildNewCase(), BuildOrder())
    };

    internal LegacyImportOpenOrderSelection ToSkippedOpenOrderSelection() =>
        new(RowKey, "skip", null, null, null);

    internal LegacyImportPlanningSelection ToPlanningSelection(string? mappedMachineId)
    {
        var createsBatch = Decision is "create_batch_to_pool" or "create_batch_and_assign";
        var assignsMachine = Decision is "assign_existing_operation" or "create_batch_and_assign";
        return new LegacyImportPlanningSelection(
            RowKey,
            Decision,
            Decision == "assign_existing_operation" ? NullIfBlank(ExistingOperation) : null,
            createsBatch ? NullIfBlank(CaseId) : null,
            createsBatch ? NullIfBlank(CaseSourceRowKey) : null,
            Decision == "create_batch_and_assign" ? NullIfBlank(RouteOperation) : null,
            createsBatch ? NullIfBlank(BatchNumber) : null,
            createsBatch ? Allocations.Select(allocation => allocation.ToContract()).ToArray() : [],
            assignsMachine ? NullIfBlank(MachineId) ?? NullIfBlank(mappedMachineId) : null,
            assignsMachine && CompatibilityOverrideConfirmed
                ? new LegacyImportCompatibilityOverride(true, NullIfBlank(CompatibilityOverrideReason))
                : null,
            createsBatch
                ? RouteOperationCandidates
                    .OrderBy(operation => operation.OperationNumber)
                    .ThenBy(operation => operation.CaseOperationId, StringComparer.Ordinal)
                    .Select(operation => new LegacyImportExpectedCaseRoute(
                        operation.CaseOperationId, operation.Version))
                    .ToArray()
                : null);
    }

    internal LegacyImportPlanningSelection ToSkippedPlanningSelection() => new(
        RowKey, "skip", null, null, null, null, null, [], null, null);

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

    private bool HasSelectedPlanningAction => Decision is "assign_existing_operation" or "create_batch_and_assign";

    private bool HasUnresolvedBlockingIssue => issues.Any(issue =>
        string.Equals(issue.Severity, "blocking", StringComparison.OrdinalIgnoreCase)
        && !LegacyExcelImportViewModel.IsCompatibilityOverrideIssue(issue));

    // The preview now contains every input used by the Server's compatibility check.
    // Older previews omit the capability collections, so those remain Server-only rather
    // than being misclassified as incompatible by a partial client comparison.
    private bool HasCompleteCompatibilityFacts => SelectedMachineCandidate is
    {
        Capabilities: not null,
        MachineTypeCapabilities: not null
    };

    private bool IsLocallyKnownIncompatible => HasCompleteCompatibilityFacts
        && !string.IsNullOrWhiteSpace(SelectedRequiredMachineType)
        && CompatibilityMatchSource is null;

    private string? CompatibilityMatchSource
    {
        get
        {
            var required = SelectedRequiredMachineType?.Trim();
            if (string.IsNullOrEmpty(required))
            {
                return "no Machine Type requirement";
            }

            var machine = SelectedMachineCandidate;
            if (!HasCompleteCompatibilityFacts || machine is null)
            {
                return null;
            }

            if (string.Equals(machine.ProcessType, required, StringComparison.OrdinalIgnoreCase))
            {
                return "process type";
            }

            if (string.Equals(machine.AxisType, required, StringComparison.OrdinalIgnoreCase))
            {
                return "axis type";
            }

            if (machine.Capabilities!.Contains(required, StringComparer.OrdinalIgnoreCase))
            {
                return "Machine capability";
            }

            return machine.MachineTypeCapabilities!.Contains(required, StringComparer.OrdinalIgnoreCase)
                ? "Machine-Type capability"
                : null;
        }
    }

    private bool HasCompleteDecision() => Kind == "planning"
        ? owner.IsPlanningActionAvailable(Decision) && (Decision switch
        {
            "assign_existing_operation" => SelectedExistingOperationCandidate is { IsAlreadyAssigned: false }
                && HasValidCompatibilityOverride(),
            "create_batch_to_pool" => HasCompleteBatchCreation(),
            "create_batch_and_assign" => HasCompleteBatchCreation()
                && SelectedRouteOperationCandidate is not null
                && string.Equals(SelectedCaseCandidate!.CaseId, SelectedRouteOperationCandidate.CaseId, StringComparison.Ordinal)
                && HasValidCompatibilityOverride(),
            _ => false
        })
        : Decision switch
        {
            "create_case" => !string.IsNullOrWhiteSpace(NewCasePartNumber)
                && !string.IsNullOrWhiteSpace(NewCaseName)
                && !string.IsNullOrWhiteSpace(NewCaseWorkingFolderPath)
                && (!HasRequestedOptionalOrder || HasCompleteOrderInput),
            "create_order" => (SelectedCaseCandidate is not null || !string.IsNullOrWhiteSpace(CaseSourceRowKey))
                && HasCompleteOrderInput,
            _ => false
        };

    private bool HasCompleteBatchCreation() => SelectedCaseCandidate is not null
        && HasCompleteReviewedCaseRoute()
        && string.IsNullOrWhiteSpace(CaseSourceRowKey)
        && !string.IsNullOrWhiteSpace(BatchNumber)
        && HasCompleteAllocations();

    private bool HasCompleteReviewedCaseRoute()
    {
        var route = RouteOperationCandidates;
        return route.Count > 0
            && route.All(operation => !string.IsNullOrWhiteSpace(operation.CaseOperationId)
                && operation.Version > 0)
            && route.Select(operation => operation.CaseOperationId)
                .Distinct(StringComparer.Ordinal).Count() == route.Count;
    }

    internal bool ApplyExplicitPatternFrom(LegacyImportRowViewModel source)
    {
        if (source.Kind != Kind || HasExplicitDecision || IsSkipped)
        {
            return false;
        }

        if (source.Decision == "skip")
        {
            Decision = "skip";
            return true;
        }

        if (Kind == "open_orders")
        {
            if (source.Decision != "create_order" || !TryFindSameCase(source, out var caseCandidate))
            {
                return false;
            }

            SelectedCaseCandidate = caseCandidate;
            Decision = "create_order";
            return true;
        }

        return source.Decision switch
        {
            // Existing Batch Operation IDs are intentionally not reusable pattern data.
            "assign_existing_operation" => false,
            "create_batch_to_pool" => ApplyPoolBatchPattern(source),
            "create_batch_and_assign" => ApplyAssignedBatchPattern(source),
            _ => false
        };
    }

    internal bool HasSamePartAndOperationShapeAs(LegacyImportRowViewModel source)
    {
        if (Kind != source.Kind || !SameText(SourcePartNumber, source.SourcePartNumber))
        {
            return false;
        }

        if (Kind != "planning")
        {
            return true;
        }

        if (!string.Equals(SectionKey, source.SectionKey, StringComparison.Ordinal))
        {
            return false;
        }

        var sourceShape = source.PatternOperationShape;
        return sourceShape is null || ExistingOperationCandidates.Any(candidate =>
                   candidate.OperationNumber == sourceShape.Value.Number
                   && SameText(candidate.Name, sourceShape.Value.Name))
               || AllRouteOperationCandidates.Any(candidate =>
                   candidate.OperationNumber == sourceShape.Value.Number
                   && SameText(candidate.Name, sourceShape.Value.Name));
    }

    private (int Number, string Name)? PatternOperationShape => SelectedExistingOperationCandidate is not null
        ? (SelectedExistingOperationCandidate.OperationNumber, SelectedExistingOperationCandidate.Name)
        : SelectedRouteOperationCandidate is not null
            ? (SelectedRouteOperationCandidate.OperationNumber, SelectedRouteOperationCandidate.Name)
            : null;

    private bool ApplyPoolBatchPattern(LegacyImportRowViewModel source)
    {
        if (!TryFindSameCase(source, out var caseCandidate))
        {
            return false;
        }

        SelectedCaseCandidate = caseCandidate;
        Decision = "create_batch_to_pool";
        CopySafePoolAllocationShapeFrom(source);
        return true;
    }

    private bool ApplyAssignedBatchPattern(LegacyImportRowViewModel source)
    {
        if (!TryFindSameCase(source, out var caseCandidate) || source.SelectedRouteOperationCandidate is null)
        {
            return false;
        }

        var route = AllRouteOperationCandidates.FirstOrDefault(candidate =>
            string.Equals(candidate.CaseId, caseCandidate.CaseId, StringComparison.Ordinal)
            && string.Equals(candidate.CaseOperationId, source.SelectedRouteOperationCandidate.CaseOperationId, StringComparison.Ordinal));
        if (route is null)
        {
            return false;
        }

        SelectedCaseCandidate = caseCandidate;
        SelectedRouteOperationCandidate = route;
        Decision = "create_batch_and_assign";
        CopySafePoolAllocationShapeFrom(source);
        return true;
    }

    // A one-line stock/scrap allocation can be reused safely when it consumed the
    // complete source row. The destination quantity is its own source quantity, not a
    // copied value. Order allocations and multi-line shapes remain for the planner.
    private void CopySafePoolAllocationShapeFrom(LegacyImportRowViewModel source)
    {
        if (Allocations.Count != 0 || SourceQuantity is not > 0 || source.SourceQuantity is not > 0
            || source.Allocations.Count != 1)
        {
            return;
        }

        var sourceAllocation = source.Allocations[0];
        if (sourceAllocation.Type is not ("stock" or "scrapAllowance")
            || sourceAllocation.ParsedQuantity != source.SourceQuantity)
        {
            return;
        }

        var allocation = new LegacyImportAllocationViewModel(OrderCandidates, () =>
        {
            owner.RowOrMappingChanged();
            RaiseStateProperties();
        })
        {
            Type = sourceAllocation.Type,
            Quantity = SourceQuantity.Value.ToString(CultureInfo.InvariantCulture)
        };
        Allocations.Add(allocation);
    }

    internal bool TryApplyBatchNumberTemplate(string template, ISet<string> reserved)
    {
        if (Decision is not ("create_batch_to_pool" or "create_batch_and_assign")
            || !string.IsNullOrWhiteSpace(BatchNumber))
        {
            return false;
        }

        var value = template.Replace("{part}", SourcePartNumber?.Trim() ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{reference}", SourceReferenceOrOrderNumber?.Trim() ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{row}", RowNumber.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Trim();
        if (string.IsNullOrWhiteSpace(value) || !reserved.Add(value))
        {
            return false;
        }

        BatchNumber = value;
        return true;
    }

    private bool TryFindSameCase(LegacyImportRowViewModel source, out LegacyImportCaseCandidate caseCandidate)
    {
        caseCandidate = null!;
        if (source.SelectedCaseCandidate is null)
        {
            return false;
        }

        caseCandidate = CaseCandidates.FirstOrDefault(candidate =>
            string.Equals(candidate.CaseId, source.SelectedCaseCandidate.CaseId, StringComparison.Ordinal))!;
        return caseCandidate is not null;
    }

    private static bool SameText(string? first, string? second) => string.Equals(
        first?.Trim(), second?.Trim(), StringComparison.OrdinalIgnoreCase);

    private bool HasValidCompatibilityOverride()
    {
        var hasReason = !string.IsNullOrWhiteSpace(CompatibilityOverrideReason)
            && CompatibilityOverrideReason.Trim().Length <= 1000;
        return RequiresCompatibilityOverride
            ? CompatibilityOverrideConfirmed && hasReason
            : !CompatibilityOverrideConfirmed || hasReason;
    }

    private LegacyImportNewCase? BuildNewCase() => !HasAnyNewCaseInput
        ? null
        : new LegacyImportNewCase(NullIfBlank(NewCasePartNumber), NullIfBlank(NewCaseName), NullIfBlank(NewCaseRevision),
            NullIfBlank(NewCaseCustomer), NullIfBlank(NewCaseCustomerReference), NullIfBlank(NewCaseWorkingFolderPath), NullIfBlank(NewCaseNotes));

    private LegacyImportOrderInput? BuildOrder() => !HasRequestedOptionalOrder && Decision != "create_order"
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

    private bool HasRequestedOptionalOrder => IncludeOrderWithNewCase || orderFieldsEdited;

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
            owner.SynchronizeCaseStageSelection(this);
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

    private bool SetOrderField(ref string field, string value, [CallerMemberName] string? name = null)
    {
        if (!SetField(ref field, value, name))
        {
            return false;
        }

        orderFieldsEdited = true;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasAnyOrderInput)));
        return true;
    }

    private void RaiseStateProperties()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasExplicitDecision)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DecisionDisplayName)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsMutation)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasAnyOrderInput)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasCompleteOrderInput)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasAnyNewCaseInput)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedRequiredMachineType)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RequiresCompatibilityOverride)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CompatibilityReviewText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RequiresMachineMapping)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowsExistingOperation)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowsCaseCandidate)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowsBatchInputs)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowsRouteOperation)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowsAllocations)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowsNewCaseInputs)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowsOrderInputs)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowsCompatibilityReview)));
        AddAllocationCommand.RaiseCanExecuteChanged();
        RemoveAllocationCommand.RaiseCanExecuteChanged();
    }

    internal void NotifyMachineMappingChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedMachineCandidate)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CompatibilityReviewText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RequiresCompatibilityOverride)));
        RaiseStateProperties();
    }

    internal void NotifyOutcomeSelectionChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActionChoices)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RequiresMachineMapping)));
        RaiseStateProperties();
    }

    private void RaiseCaseSelectionProperties()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RouteOperationCandidates)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AvailableRouteOperationCandidates)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedCaseRouteOperationCount)));
        RaiseStateProperties();
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static int? ParseInt(string? value) => int.TryParse(value, NumberStyles.Integer,
        CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    private static string NormalizeDate(string? value) => DateOnly.TryParse(value, CultureInfo.InvariantCulture,
        DateTimeStyles.AllowWhiteSpaces, out var parsed)
        ? parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        : value?.Trim() ?? string.Empty;
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
