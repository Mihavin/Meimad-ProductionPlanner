using System.Text.Json;
using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Presentation;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

public sealed class LocalAiPanelTests
{
    [Fact]
    public async Task Shop_machine_selection_clears_prior_answer_and_signout_disables_summary()
    {
        var api = new AiApi(); var shop = new ShopOverviewViewModel(); shop.AttachSession(api);
        Assert.False(shop.Ai.ShowRunControls);
        shop.SelectedMachine = new("machine-a", "1", "First", "UNKNOWN", "UNKNOWN", "UNKNOWN", null, 0, "", []);
        Assert.Equal("machine-a", shop.Ai.MachineId);
        await shop.Ai.RunAsync(true);
        Assert.NotNull(shop.Ai.Answer);
        shop.SelectedMachine = new("machine-b", "2", "Second", "UNKNOWN", "UNKNOWN", "UNKNOWN", null, 0, "", []);
        Assert.Null(shop.Ai.Answer); Assert.Equal("machine-b", shop.Ai.MachineId);
        shop.AttachSession(null);
        Assert.False(shop.Ai.SummarizeCommand.CanExecute(null));
    }

    [Fact]
    public async Task Revoked_source_recheck_clears_inspection_and_ignores_its_late_response()
    {
        var api = new AiApi();
        var model = new LocalAiPanelViewModel(); model.AttachSession(api);
        await model.RunAsync(false);
        var pending = model.InspectAsync(api.Answer.Evidence.Facts[0]);
        var source = api.Inspections.Dequeue();
        var check = model.RecheckAsync();
        api.Checks.Dequeue().SetResult(new("request", "SOURCE_UNAVAILABLE", api.At));
        await check;
        source.SetResult(api.Inspection(0)); await pending;
        Assert.Null(model.Answer);
        Assert.Null(model.InspectedEvidence);
        Assert.Contains("source or answer unavailable", model.Status);
    }

    [Fact]
    public async Task Older_inspection_cannot_replace_the_last_clicked_source()
    {
        var api = new AiApi(); var model = new LocalAiPanelViewModel(); model.AttachSession(api);
        await model.RunAsync(false);
        var first = model.InspectAsync(api.Answer.Evidence.Facts[0]); var firstResult = api.Inspections.Dequeue();
        var second = model.InspectAsync(api.Answer.Evidence.Facts[1]); var secondResult = api.Inspections.Dequeue();
        secondResult.SetResult(api.Inspection(1)); await second;
        firstResult.SetResult(api.Inspection(0)); await first;
        Assert.Equal("E2", model.InspectedEvidence!.Fact.EvidenceId);
    }

    [Fact]
    public async Task Older_source_check_cannot_restore_answer_after_newer_revocation()
    {
        var api = new AiApi(); var model = new LocalAiPanelViewModel(); model.AttachSession(api);
        await model.RunAsync(false);
        var first = model.RecheckAsync(); var firstResult = api.Checks.Dequeue();
        var second = model.RecheckAsync(); var secondResult = api.Checks.Dequeue();
        secondResult.SetResult(new("request", "SOURCE_UNAVAILABLE", api.At)); await second;
        firstResult.SetResult(new("request", "COMPLETED", api.At, api.Answer)); await first;
        Assert.Null(model.Answer);
    }

    private sealed class AiApi : StubPlannerApiClient, IPlannerApiClient
    {
        internal readonly DateTimeOffset At = DateTimeOffset.UtcNow;
        internal readonly LocalExplanation Answer;
        internal Queue<TaskCompletionSource<EvidenceInspection>> Inspections = [];
        internal Queue<TaskCompletionSource<LocalAiRequestStatus>> Checks = [];
        internal AiApi()
        {
            var facts = Enumerable.Range(1, 2).Select(i => new EvidenceFact("E" + i, "Document", "excerpt",
                JsonSerializer.SerializeToElement("source text"), null, new("document", "doc" + i), "hash", At, null,
                "HISTORICAL_REFERENCE", "Approved excerpt")).ToArray();
            Answer = new("request", "DETERMINISTIC", new(1, "FindSimilarIncidents", At, "revision", null, null, At.AddHours(-1), At,
                facts, [], []), []);
        }
        internal EvidenceInspection Inspection(int index) => new(Answer.Evidence.Facts[index], "HISTORICAL_REFERENCE", At, At);
        public Task<LocalAiRequestStatus> SubmitLocalAiAsync(ExplanationRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new LocalAiRequestStatus("request", "COMPLETED", At, Answer));
        public Task<LocalAiRequestStatus> GetLocalAiAsync(string id, CancellationToken cancellationToken = default)
        {
            var completion = new TaskCompletionSource<LocalAiRequestStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
            Checks.Enqueue(completion); return completion.Task;
        }
        public Task<EvidenceInspection> InspectLocalAiEvidenceAsync(string id, string evidenceId, CancellationToken cancellationToken = default)
        {
            var completion = new TaskCompletionSource<EvidenceInspection>(TaskCreationOptions.RunContinuationsAsynchronously);
            Inspections.Enqueue(completion); return completion.Task;
        }
    }
}
