using System.ComponentModel;
using System.Globalization;
using Meimad.Planner.Client.Windows.Api;
namespace Meimad.Planner.Client.Windows.Presentation;

internal sealed class LocalAiPanelViewModel : INotifyPropertyChanged
{
    private IPlannerApiClient? api;
    private CancellationTokenSource? cancel;
    private string? requestId;
    private int generation;
    private int inspectionGeneration;
    private int recheckGeneration;
    private int feedbackVersion;
    private bool savingFeedback;
    public bool ShowRunControls { get; init; } = true;
    public string Correction { get; set; } = "";
    public string FeedbackStatus { get; private set; } = "";
    public AsyncCommand UsefulCommand { get; }
    public AsyncCommand NotUsefulCommand { get; }
    public string RunId { get; set; } = "";
    public string MachineId { get; set; } = "";
    public string SearchTerm { get; set; } = "";
    public string FromUtc { get; set; } = DateTimeOffset.UtcNow.AddHours(-8).ToString("O");
    public string ToUtc { get; set; } = DateTimeOffset.UtcNow.ToString("O");
    public string Status { get; private set; } = "AI assistance uses Server evidence. Local inference is optional.";
    public LocalExplanation? Answer { get; private set; }
    public AsyncCommand ExplainCommand { get; }
    public AsyncCommand SummarizeCommand { get; }
    public AsyncCommand CancelCommand { get; }
    public AsyncCommand SimilarCommand { get; }
    public AsyncCommand RecheckCommand { get; }
    public AsyncCommand<EvidenceFact> InspectEvidenceCommand { get; }
    public EvidenceInspection? InspectedEvidence { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public LocalAiPanelViewModel()
    {
        ExplainCommand = new(() => RunAsync(false), () => ShowRunControls && api is not null && cancel is null);
        SummarizeCommand = new(() => RunAsync(true), () => api is not null && cancel is null);
        CancelCommand = new(CancelAsync, () => cancel is not null);
        SimilarCommand = new(() => RunAsync(true, true), () => api is not null && cancel is null);
        RecheckCommand = new(RecheckAsync, () => api is not null && Answer is not null && cancel is null);
        InspectEvidenceCommand = new(InspectAsync, fact => api is not null && Answer is not null && fact is not null && cancel is null);
        UsefulCommand = new(() => FeedbackAsync(true), () => api is not null && Answer is not null && !savingFeedback);
        NotUsefulCommand = new(() => FeedbackAsync(false), () => api is not null && Answer is not null && !savingFeedback);
    }
    internal void AttachSession(IPlannerApiClient? client)
    {
        generation++; cancel?.Cancel(); cancel = null; requestId = null; api = client; InspectedEvidence = null;
        Answer = null; RunId = ""; MachineId = ""; SearchTerm = ""; Correction = ""; FeedbackStatus = ""; feedbackVersion = 0; savingFeedback = false; Status = "AI assistance uses Server evidence. Local inference is optional."; Changed();
    }
    internal async Task ExplainAsync(string runId)
    { await CancelAsync(); RunId = runId; Changed(); await RunAsync(false); }
    internal void SelectMachineScope(string? machineId)
    { AttachSession(api); MachineId = machineId ?? ""; Changed(); }
    internal async Task RunAsync(bool shift, bool historical = false)
    {
        if (api is not { } client || cancel is not null) return;
        var current = ++generation; var source = new CancellationTokenSource(TimeSpan.FromMinutes(3)); cancel = source;
        Answer = null; InspectedEvidence = null; requestId = null; Correction = ""; FeedbackStatus = ""; feedbackVersion = 0; savingFeedback = false; Changed();
        try
        {
            DateTimeOffset? from = null, to = null;
            if (shift)
            {
                static DateTimeOffset Instant(string text)
                {
                    if (!text.Contains('T') || !(text.EndsWith('Z') || text.LastIndexOf('+') > 10 || text.LastIndexOf('-') > 10)
                        || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value))
                        throw new ArgumentException("Use an ISO instant with Z or an explicit UTC offset.");
                    return value;
                }
                from = Instant(FromUtc); to = Instant(ToUtc);
            }
            var request = new ExplanationRequest(historical ? "FindSimilarIncidents" : shift ? "SummarizeShift" : "ExplainBlockers", shift ? null : RunId.Trim(), from, to,
                string.IsNullOrWhiteSpace(MachineId) ? null : MachineId.Trim(), SearchTerm: historical ? SearchTerm.Trim() : null);
            var work = await client.SubmitLocalAiAsync(request, source.Token);
            if (current != generation) { await client.CancelLocalAiAsync(work.RequestId); return; }
            requestId = work.RequestId;
            while (work.Status is "QUEUED" or "RUNNING")
            {
                Status = work.Status; Changed();
                await Task.Delay(500, source.Token);
                work = await client.GetLocalAiAsync(work.RequestId, source.Token);
                if (current != generation) return;
            }
            Answer = work.Result;
            Status = Answer is null ? $"{work.Status}: {work.ResultCode}. Refresh to request current evidence."
                : Describe(Answer);
        }
        catch (OperationCanceledException) { if (current == generation) Status = "Cancelled or timed out."; }
        catch (Exception exception) { if (current == generation) Status = "Explanation unavailable: " + exception.Message; }
        finally
        {
            if (current == generation) { cancel = null; requestId = null; Changed(); }
            source.Dispose();
        }
    }
    internal async Task CancelAsync()
    {
        var id = requestId; var client = api;
        var current = ++generation; cancel?.Cancel(); cancel = null; requestId = null; Answer = null; InspectedEvidence = null; Status = "Cancelled."; Changed();
        if (id is not null && client is not null)
            try { await client.CancelLocalAiAsync(id); } catch (Exception) { if (current == generation) { Status = "Cancellation was not confirmed; the Server timeout remains in effect."; Changed(); } }
    }
    internal async Task RecheckAsync()
    {
        if (api is not { } client || Answer is not { } prior || cancel is not null) return;
        var current = generation;
        var check = ++recheckGeneration;
        inspectionGeneration++; InspectedEvidence = null; Changed();
        try
        {
            var work = await client.GetLocalAiAsync(prior.RequestId);
            if (current != generation || check != recheckGeneration) return;
            inspectionGeneration++; InspectedEvidence = null;
            Answer = work.Result;
            Status = Answer is null ? $"{work.Status}: source or answer unavailable; request a new explanation." : Describe(Answer);
        }
        catch (Exception exception) { if (current == generation && check == recheckGeneration) Status = "Source check unavailable: " + exception.Message; }
        finally { if (current == generation && check == recheckGeneration) Changed(); }
    }
    internal async Task InspectAsync(EvidenceFact? fact)
    {
        if (api is not { } client || Answer is not { } answer || fact is null || !answer.Evidence.Facts.Contains(fact)) return;
        var current = generation; var inspection = ++inspectionGeneration; InspectedEvidence = null; Changed();
        try
        {
            var source = await client.InspectLocalAiEvidenceAsync(answer.RequestId, fact.EvidenceId);
            if (current == generation && inspection == inspectionGeneration) InspectedEvidence = source;
        }
        catch (Exception exception) { if (current == generation && inspection == inspectionGeneration) Status = "Evidence is unavailable or changed: " + exception.Message; }
        finally { if (current == generation && inspection == inspectionGeneration) Changed(); }
    }
    private static string Describe(LocalExplanation answer) => $"{answer.Mode}. {answer.Currency}. Evidence sampled {answer.Evidence.AsOfUtc:u}; checked {answer.CheckedAt:u}. Refresh before acting. {answer.FallbackReason}";
    internal async Task FeedbackAsync(bool useful)
    {
        if (api is not { } client || Answer is not { } answer || savingFeedback) return;
        var current = generation; savingFeedback = true; Changed();
        try
        {
            var feedback = await client.SaveLocalAiFeedbackAsync(answer.RequestId, feedbackVersion, useful, Correction);
            if (current != generation) return;
            feedbackVersion = feedback.Version;
            FeedbackStatus = $"Feedback saved at {feedback.UpdatedAt:u}. Production facts are unchanged.";
        }
        catch (Exception exception) { if (current == generation) FeedbackStatus = exception.Message; }
        finally { if (current == generation) { savingFeedback = false; Changed(); } }
    }
    private void Changed()
    {
        PropertyChanged?.Invoke(this, new(null)); ExplainCommand.RaiseCanExecuteChanged(); SummarizeCommand.RaiseCanExecuteChanged(); CancelCommand.RaiseCanExecuteChanged(); SimilarCommand.RaiseCanExecuteChanged(); RecheckCommand.RaiseCanExecuteChanged();
        UsefulCommand.RaiseCanExecuteChanged(); NotUsefulCommand.RaiseCanExecuteChanged();
        InspectEvidenceCommand.RaiseCanExecuteChanged();
    }
}
