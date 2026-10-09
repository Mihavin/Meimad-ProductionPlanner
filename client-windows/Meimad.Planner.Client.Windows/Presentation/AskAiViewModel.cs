using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Localization;

namespace Meimad.Planner.Client.Windows.Presentation;

/// <summary>One line of the conversation; <c>Speaker</c> keeps the role readable without colour.</summary>
internal sealed record AskAiMessage(bool IsUser, string Speaker, string Text, string Sources, bool IsError = false)
{
    public bool HasSources => Sources.Length > 0;
}

/// <summary>The current selection the window sends with a question, and how it is shown to the user.</summary>
internal sealed record AskAiSelection(AiChatContext? Context, string Description);

/// <summary>
/// The Ask AI window: free-text questions answered by the Server's local AI from Planner data and approved
/// documents, optionally about the user's current selection. The assistant is read-only; the Server runs
/// every lookup and decides what the model may see.
/// </summary>
internal sealed class AskAiViewModel : INotifyPropertyChanged
{
    private const int MaxHistory = 20;
    private const int MaxMessageCharacters = 4000;
    private readonly Func<IPlannerApiClient?> client;
    private readonly Func<AskAiSelection> selection;
    private readonly TimeSpan pollInterval;
    private CancellationTokenSource? running;
    private string? chatId;
    private string question = "";
    private bool useSelection = true;

    internal AskAiViewModel(Func<IPlannerApiClient?> client, Func<AskAiSelection> selection, TimeSpan? pollInterval = null)
    {
        this.client = client;
        this.selection = selection;
        this.pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(700);
        SendCommand = new(SendAsync, () => running is null && !string.IsNullOrWhiteSpace(Question) && client() is not null);
        StopCommand = new(StopAsync, () => running is not null);
        NewChatCommand = new(NewChatAsync, () => Messages.Count > 0 || running is not null);
        RefreshSelection();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<AskAiMessage> Messages { get; } = [];
    public AsyncCommand SendCommand { get; }
    public AsyncCommand StopCommand { get; }
    public AsyncCommand NewChatCommand { get; }
    public string Status { get; private set; } = "Ask about a part, an operation, NC programs, material or documents.";
    public string SelectionDescription { get; private set; } = "";
    public bool IsBusy => running is not null;

    public string Question
    {
        get => question;
        set { question = value ?? ""; Changed(nameof(Question)); SendCommand.RaiseCanExecuteChanged(); }
    }

    public bool UseSelection
    {
        get => useSelection;
        set { useSelection = value; RefreshSelection(); }
    }

    /// <summary>Reads the main window's selection again (the window calls this whenever it is activated).</summary>
    internal void RefreshSelection()
    {
        var current = selection();
        SelectionDescription = useSelection ? current.Description : Translate("Not sent: general question");
        Changed(nameof(SelectionDescription)); Changed(nameof(UseSelection));
    }

    internal async Task SendAsync()
    {
        if (client() is not { } api || running is not null || string.IsNullOrWhiteSpace(Question)) return;
        var text = Question.Trim();
        if (text.Length > MaxMessageCharacters) text = text[..MaxMessageCharacters];
        var history = Messages.Where(message => !message.IsError).TakeLast(MaxHistory - 1)
            .Select(message => new AiChatTurn(message.IsUser ? "user" : "assistant", message.Text.Length > MaxMessageCharacters ? message.Text[..MaxMessageCharacters] : message.Text))
            .SkipWhile(turn => turn.Role == "assistant")
            .Append(new AiChatTurn("user", text)).ToList();
        var current = selection();
        var context = useSelection ? current.Context : null;
        SelectionDescription = useSelection ? current.Description : Translate("Not sent: general question");
        Messages.Add(new(true, Translate("You"), text, ""));
        Question = "";
        using var source = new CancellationTokenSource(TimeSpan.FromMinutes(16));
        running = source;
        SetStatus(Translate("Sending the question…"));
        try
        {
            var work = await api.AskAiAsync(history, context, source.Token);
            chatId = work.ChatId;
            while (work.Status is "QUEUED" or "RUNNING")
            {
                SetStatus(work.Progress ?? Translate("The assistant is working…"));
                await Task.Delay(pollInterval, source.Token);
                work = await api.GetAiChatAsync(work.ChatId, source.Token);
            }
            if (work.Status == "COMPLETED")
            {
                var sources = work.Sources is { Count: > 0 } list ? Translate("Based on:") + " " + string.Join("; ", list.Select(item => item.Title)) : "";
                Messages.Add(new(false, Translate("Assistant"), work.Answer ?? "", sources));
                SetStatus(Translate("Answered by the local AI. Check the source before acting on it."));
            }
            else
            {
                var reason = work.Status switch
                {
                    "CANCELLED" => Translate("The question was cancelled."),
                    "EXPIRED" => Translate("The assistant was busy too long. Ask again."),
                    "INTERRUPTED" => Translate("The Server restarted. Ask again."),
                    _ => work.Answer ?? (work.ResultCode == "timed_out" ? Translate("The assistant took too long. Ask a narrower question.") : Translate("The assistant could not answer.")),
                };
                Messages.Add(new(false, Translate("Assistant"), reason, "", IsError: true));
                SetStatus(reason);
            }
        }
        catch (OperationCanceledException) { SetStatus(Translate("Stopped.")); }
        catch (PlannerApiException exception)
        {
            Messages.Add(new(false, Translate("Assistant"), exception.Message, "", IsError: true));
            SetStatus(exception.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                ? Translate("The assistant is busy with other questions. Try again shortly.") : exception.Message);
        }
        catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException)
        {
            SetStatus(Translate("The Server could not be reached:") + " " + exception.Message);
        }
        finally
        {
            running = null; chatId = null;
            Changed(nameof(IsBusy)); RaiseCommands();
        }
    }

    internal async Task StopAsync()
    {
        var id = chatId;
        running?.Cancel();
        if (id is not null && client() is { } api)
            try { await api.CancelAiChatAsync(id); } catch (Exception exception) when (exception is HttpRequestException or PlannerApiException) { }
    }

    internal async Task NewChatAsync()
    {
        await StopAsync();
        Messages.Clear();
        SetStatus(Translate("New conversation."));
        RaiseCommands();
    }

    private void SetStatus(string text)
    {
        Status = text;
        Changed(nameof(Status)); Changed(nameof(IsBusy)); RaiseCommands();
    }

    private void RaiseCommands()
    {
        SendCommand.RaiseCanExecuteChanged(); StopCommand.RaiseCanExecuteChanged(); NewChatCommand.RaiseCanExecuteChanged();
    }

    private static string Translate(string text) => LocalizationService.Current.Translate(text);
    private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
}
