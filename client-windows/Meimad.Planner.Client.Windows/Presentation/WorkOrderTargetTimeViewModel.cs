using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using Meimad.Planner.Client.Windows.Api;

namespace Meimad.Planner.Client.Windows.Presentation;

/// <summary>A resource row of the target time; status is text and an icon, never colour alone.</summary>
internal sealed record TargetTimeRow(
    string Resource,
    string KindText,
    string Operations,
    string HourlyRateText,
    string TargetHoursText,
    string TargetMinutesPerPartText,
    string PlannedHoursText,
    string Verdict);

/// <summary>
/// The target time of the selected Work Order (owner request 2026-10-14): from the part price, the Work Order's material
/// total and each route resource's work-hour price, how many hours the margin buys on that resource. The Server
/// calculates and stores it; this view model only collects the inputs and shows the result.
/// </summary>
internal sealed class WorkOrderTargetTimeViewModel : INotifyPropertyChanged
{
    private IPlannerApiClient? apiClient;
    private bool isEditor;
    private bool isBusy;
    private string? batchId;
    private WorkOrderTargetTime? saved;
    private string partPrice = string.Empty;
    private string materialTotal = string.Empty;
    private string notes = string.Empty;
    private string summary = string.Empty;
    private string savedText = string.Empty;
    private string status = "Select a Work Order to calculate its target time.";

    internal WorkOrderTargetTimeViewModel()
    {
        CalculateCommand = new AsyncCommand(CalculateAsync, () => apiClient is not null && batchId is not null && !isBusy);
        SaveCommand = new AsyncCommand(SaveAsync, () => CanChange);
        DeleteCommand = new AsyncCommand(DeleteAsync, () => CanChange && saved is not null);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<TargetTimeRow> Rows { get; } = [];
    public AsyncCommand CalculateCommand { get; }
    public AsyncCommand SaveCommand { get; }
    public AsyncCommand DeleteCommand { get; }

    /// <summary>Price of one part in NIS (suggested from the Work Order's Orders).</summary>
    public string PartPrice { get => partPrice; set => Set(ref partPrice, value); }

    /// <summary>Material cost of the whole Work Order in NIS.</summary>
    public string MaterialTotal { get => materialTotal; set => Set(ref materialTotal, value); }

    public string Notes { get => notes; set => Set(ref notes, value); }
    public string Summary { get => summary; private set => Set(ref summary, value); }
    public string SavedText { get => savedText; private set => Set(ref savedText, value); }
    public string Status { get => status; private set => Set(ref status, value); }

    private bool CanChange => isEditor && apiClient is not null && batchId is not null && !isBusy;

    internal void AttachSession(IPlannerApiClient? client, bool editor)
    {
        apiClient = client;
        isEditor = editor;
        RaiseCommands();
    }

    internal async Task LoadAsync(ProductionBatch? batch)
    {
        batchId = batch?.BatchId;
        saved = null;
        Rows.Clear();
        Summary = SavedText = string.Empty;
        PartPrice = MaterialTotal = Notes = string.Empty;
        RaiseCommands();
        if (batch is null || apiClient is null)
        {
            Status = "Select a Work Order to calculate its target time.";
            return;
        }
        try
        {
            var overview = await apiClient.GetWorkOrderTargetTimeAsync(batch.BatchId);
            if (batchId != batch.BatchId) return;
            Apply(overview);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            Status = exception.Message;
        }
        RaiseCommands();
    }

    private void Apply(WorkOrderTargetTimeOverview overview)
    {
        saved = overview.Saved;
        if (saved is { } current)
        {
            PartPrice = Number(current.Calculation.PartPriceNis);
            MaterialTotal = Number(current.Calculation.MaterialTotalNis);
            Notes = current.Notes ?? string.Empty;
            ShowCalculation(current.Calculation);
            SavedText = $"Saved by {current.UpdatedBy ?? "unknown"} on {current.UpdatedAt.ToLocalTime():g} with the hourly prices of that day.";
            Status = "Change the prices and Calculate to compare, then Save to replace the saved target time.";
        }
        else
        {
            PartPrice = overview.SuggestedPartPriceNis is { } suggested ? Number(suggested) : string.Empty;
            Rows.Clear();
            foreach (var resource in overview.Resources)
                Rows.Add(new TargetTimeRow(resource.Name, KindText(resource.Kind), Operations(resource.OperationNumbers),
                    Rate(resource.HourlyRateNis, resource.DefaultRate), "—", "—", "—", string.Empty));
            Summary = $"Quantity {overview.Quantity}. Enter the material total and Calculate.";
            SavedText = "No target time saved for this Work Order.";
            Status = overview.Resources.Count == 0
                ? "The Work Order's route has no Machine or station step to price."
                : overview.SuggestedPartPriceNis is null
                    ? "Enter the part price (the Work Order's Orders have no price) and the material total."
                    : "The part price is taken from the Work Order's Orders; check it and enter the material total.";
        }
    }

    internal async Task CalculateAsync()
    {
        if (!TryInputs(out var input)) return;
        await RunAsync(async () =>
        {
            ShowCalculation(await apiClient!.CalculateWorkOrderTargetTimeAsync(batchId!, input));
            Status = "Calculated with today's hourly prices; not saved.";
        });
    }

    internal async Task SaveAsync()
    {
        if (!TryInputs(out var input)) return;
        await RunAsync(async () =>
        {
            try
            {
                saved = await apiClient!.SaveWorkOrderTargetTimeAsync(batchId!, input, saved?.EntityTag);
                ShowCalculation(saved.Calculation);
                SavedText = $"Saved by {saved.UpdatedBy ?? "you"} on {saved.UpdatedAt.ToLocalTime():g}.";
                Status = "Target time saved on the Work Order for every user.";
            }
            catch (PlannerApiException exception) when (exception.StatusCode == HttpStatusCode.PreconditionFailed)
            {
                var who = exception.Conflict?.ChangedBy is { Length: > 0 } changedBy ? $" by {changedBy}" : string.Empty;
                var id = batchId!;
                Apply(await apiClient!.GetWorkOrderTargetTimeAsync(id));
                Status = $"Not saved: the target time was changed{who} after you opened it. The current values are loaded; check them and save again.";
            }
        });
    }

    internal async Task DeleteAsync()
    {
        if (saved is null) return;
        await RunAsync(async () =>
        {
            await apiClient!.DeleteWorkOrderTargetTimeAsync(batchId!, saved.EntityTag);
            Apply(await apiClient.GetWorkOrderTargetTimeAsync(batchId!));
            Status = "Saved target time removed.";
        });
    }

    private async Task RunAsync(Func<Task> action)
    {
        isBusy = true;
        RaiseCommands();
        try
        {
            await action();
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            Status = exception.Message;
        }
        finally
        {
            isBusy = false;
            RaiseCommands();
        }
    }

    private bool TryInputs(out WorkOrderTargetTimeInput input)
    {
        input = new WorkOrderTargetTimeInput(0, 0);
        if (!TryParse(PartPrice, out var price))
        {
            Status = "Enter the price of one part in NIS.";
            return false;
        }
        if (!TryParse(MaterialTotal, out var material))
        {
            Status = "Enter the material cost of the whole Work Order in NIS (0 when there is none).";
            return false;
        }
        input = new WorkOrderTargetTimeInput(price, material, string.IsNullOrWhiteSpace(Notes) ? null : Notes.Trim());
        return true;
    }

    private void ShowCalculation(TargetTimeCalculation calculation)
    {
        Rows.Clear();
        foreach (var resource in calculation.Resources)
        {
            var verdict = calculation.MarginNis <= 0
                ? "✖ No margin"
                : resource.PlannedHours <= 0
                    ? string.Empty
                    : resource.PlannedHours <= resource.TargetHours
                        ? "✔ Planned within target"
                        : "⚠ Planned over target";
            Rows.Add(new TargetTimeRow(resource.Name, KindText(resource.Kind), Operations(resource.OperationNumbers),
                Rate(resource.HourlyRateNis, resource.DefaultRate), Hours(resource.TargetHours),
                resource.TargetMinutesPerPart.ToString("0.#", CultureInfo.CurrentCulture),
                resource.PlannedHours > 0 ? Hours(resource.PlannedHours) : "—", verdict));
        }
        Summary = string.Create(CultureInfo.CurrentCulture,
            $"Margin {calculation.MarginNis:N2} NIS = {calculation.PartPriceNis:N2} × {calculation.Quantity} parts − material {calculation.MaterialTotalNis:N2}. Each row: the whole margin at that resource's hourly price.");
    }

    internal static bool TryParse(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var trimmed = text.Trim();
        return (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
                || double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
               && double.IsFinite(value) && value >= 0;
    }

    private static string KindText(string kind) => kind switch
    {
        "MACHINE" => "Machine",
        "MACHINE_TYPE" => "Machine type (not assigned)",
        "WORKSTATION_TYPE" => "Station type",
        _ => kind,
    };

    private static string Operations(IReadOnlyList<int> numbers) => string.Join(", ", numbers);

    private static string Rate(double rate, bool isDefault) =>
        rate.ToString("0.##", CultureInfo.CurrentCulture) + (isDefault ? " (default)" : string.Empty);

    private static string Hours(double hours) => hours.ToString("0.##", CultureInfo.CurrentCulture);

    private static string Number(double value) => value.ToString("0.##", CultureInfo.CurrentCulture);

    private static bool IsExpected(Exception exception) =>
        exception is PlannerApiException or System.Net.Http.HttpRequestException or TaskCanceledException or NotSupportedException;

    private void RaiseCommands()
    {
        CalculateCommand.RaiseCanExecuteChanged();
        SaveCommand.RaiseCanExecuteChanged();
        DeleteCommand.RaiseCanExecuteChanged();
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
