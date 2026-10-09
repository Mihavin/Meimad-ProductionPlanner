using System.Net;
using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Presentation;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

public sealed class AskAiViewModelTests
{
    [Fact]
    public async Task Question_carries_history_and_the_current_selection_and_shows_the_answer_with_sources()
    {
        var api = new ChatApi();
        var selection = new AskAiSelection(new AiChatContext(CaseId: "case-1", GCodeReleaseId: "release-1"), "PN-1 → OP10");
        var model = new AskAiViewModel(() => api, () => selection, TimeSpan.FromMilliseconds(1));
        Assert.Equal("PN-1 → OP10", model.SelectionDescription);
        Assert.False(model.SendCommand.CanExecute(null));

        model.Question = "  What material is this?  ";
        api.Replies.Enqueue(new("chat-1", "RUNNING", api.At, "Reading Case PN-1"));
        api.Replies.Enqueue(new("chat-1", "COMPLETED", api.At, Answer: "7050-T7451.", Sources: [new("CONTEXT", "Case PN-1"), new("DOCUMENT", "PL.pdf")]));
        await model.SendAsync();
        var (messages, context) = api.Asked.Single();
        Assert.Equal([new AiChatTurn("user", "What material is this?")], messages);
        Assert.Equal("release-1", context!.GCodeReleaseId);
        Assert.Equal(2, model.Messages.Count);
        Assert.Equal("7050-T7451.", model.Messages[1].Text);
        Assert.Contains("PL.pdf", model.Messages[1].Sources, StringComparison.Ordinal);
        Assert.Equal("", model.Question);
        Assert.False(model.IsBusy);

        // A follow-up sends the conversation; switching the selection off sends a general question.
        model.UseSelection = false;
        model.Question = "And the raw stock?";
        api.Replies.Enqueue(new("chat-2", "COMPLETED", api.At, Answer: "5.5 IN plate."));
        await model.SendAsync();
        var (followUp, general) = api.Asked[1];
        Assert.Equal(["user", "assistant", "user"], followUp.Select(turn => turn.Role));
        Assert.Null(general);
    }

    [Fact]
    public async Task Refusals_and_failures_are_shown_as_errors_and_not_resent_as_history()
    {
        var api = new ChatApi();
        var model = new AskAiViewModel(() => api, () => new(null, "Nothing"), TimeSpan.FromMilliseconds(1));
        api.Refusal = new PlannerApiException(HttpStatusCode.Conflict, "ai_unavailable", "The AI assistant is switched off.");
        model.Question = "Hello?";
        await model.SendAsync();
        Assert.True(model.Messages[1].IsError);
        Assert.Contains("switched off", model.Status, StringComparison.Ordinal);

        api.Refusal = null;
        api.Replies.Enqueue(new("chat-3", "FAILED", api.At, Answer: "The local AI runtime did not answer.", ResultCode: "ai_runtime_unavailable"));
        model.Question = "Again?";
        await model.SendAsync();
        Assert.True(model.Messages[^1].IsError);
        Assert.Equal(["user", "user"], api.Asked.Single().Messages.Select(turn => turn.Role));

        await model.NewChatAsync();
        Assert.Empty(model.Messages);
        var signedOut = new AskAiViewModel(() => null, () => new(null, "Nothing")) { Question = "Hi" };
        Assert.False(signedOut.SendCommand.CanExecute(null));
    }

    private sealed class ChatApi : StubPlannerApiClient, IPlannerApiClient
    {
        internal readonly DateTimeOffset At = DateTimeOffset.UtcNow;
        internal readonly Queue<AiChatStatus> Replies = [];
        internal readonly List<(IReadOnlyList<AiChatTurn> Messages, AiChatContext? Context)> Asked = [];
        internal PlannerApiException? Refusal;
        public Task<AiChatStatus> AskAiAsync(IReadOnlyList<AiChatTurn> messages, AiChatContext? context, CancellationToken cancellationToken = default)
        {
            if (Refusal is { } refusal) throw refusal;
            Asked.Add((messages.ToList(), context));
            return Task.FromResult(Replies.Dequeue());
        }
        public Task<AiChatStatus> GetAiChatAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult(Replies.Dequeue());
        public Task CancelAiChatAsync(string id, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
