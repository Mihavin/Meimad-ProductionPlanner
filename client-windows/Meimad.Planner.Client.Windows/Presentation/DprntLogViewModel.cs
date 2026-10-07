using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using Meimad.Planner.Client.Windows.Api;

namespace Meimad.Planner.Client.Windows.Presentation;

/// <summary>One Machine to choose in the DPRNT log.</summary>
internal sealed record DprntLogMachineChoice(string MachineId, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Reports → DPRNT Log: every line a Machine's DPRNT output sent (schema v96), in arrival order,
/// for a chosen period and text, with an export to a text file. Read-only.
/// </summary>
internal sealed class DprntLogViewModel : INotifyPropertyChanged
{
    internal const int PageSize = 10000;
    internal const int MaximumLines = 200000;

    private readonly Action<string> openFile;
    private IPlannerApiClient? apiClient;
    private DprntLogMachineChoice? machine;
    private DateTime? from;
    private DateTime? to;
    private string search = string.Empty;
    private bool isBusy;
    private string status = "Choose a machine and press Load log. Without dates the whole log is loaded.";
    private string? loadedMachineLabel;

    internal DprntLogViewModel(Action<string>? openFile = null)
    {
        this.openFile = openFile ?? (path => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }));
        LoadCommand = new AsyncCommand(LoadAsync, () => apiClient is not null && machine is not null && !IsBusy);
        ExportCommand = new AsyncCommand(ExportAsync, () => Lines.Count > 0 && !IsBusy);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<DprntLogMachineChoice> Machines { get; } = [];

    public ObservableCollection<DprntLogLineInfo> Lines { get; } = [];

    public AsyncCommand LoadCommand { get; }
    public AsyncCommand ExportCommand { get; }

    public DprntLogMachineChoice? Machine
    {
        get => machine;
        set
        {
            if (SetField(ref machine, value)) LoadCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>The first day (local); empty means from the start of the log.</summary>
    public DateTime? From
    {
        get => from;
        set => SetField(ref from, value);
    }

    /// <summary>The last day (local, included); empty means up to now.</summary>
    public DateTime? To
    {
        get => to;
        set => SetField(ref to, value);
    }

    public string Search
    {
        get => search;
        set => SetField(ref search, value ?? string.Empty);
    }

    public string Status
    {
        get => status;
        private set => SetField(ref status, value);
    }

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (!SetField(ref isBusy, value)) return;
            LoadCommand.RaiseCanExecuteChanged();
            ExportCommand.RaiseCanExecuteChanged();
        }
    }

    internal void AttachSession(IPlannerApiClient? client)
    {
        var changed = !ReferenceEquals(apiClient, client);
        apiClient = client;
        LoadCommand.RaiseCanExecuteChanged();
        if (apiClient is not null && (changed || Machines.Count == 0)) _ = LoadMachinesAsync();
    }

    internal async Task LoadMachinesAsync()
    {
        if (apiClient is null) return;
        try
        {
            var machines = await apiClient.ListMachinesAsync();
            var selected = machine?.MachineId;
            Machines.Clear();
            foreach (var value in machines.OrderBy(value => value.Number, StringComparer.CurrentCultureIgnoreCase))
                Machines.Add(new DprntLogMachineChoice(value.MachineId,
                    string.IsNullOrWhiteSpace(value.Number) || value.Number == value.Name ? value.Name : $"{value.Number} - {value.Name}"));
            Machine = Machines.FirstOrDefault(value => value.MachineId == selected);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            Status = Friendly(exception);
        }
    }

    /// <summary>Loads every matching line page by page, up to <see cref="MaximumLines"/>.</summary>
    internal async Task LoadAsync()
    {
        if (apiClient is null || machine is null) return;
        if (From is DateTime start && To is DateTime end && end.Date < start.Date)
        {
            Status = "The last day is before the first day.";
            return;
        }

        var choice = machine;
        IsBusy = true;
        Status = "Loading the DPRNT log...";
        Lines.Clear();
        ExportCommand.RaiseCanExecuteChanged();
        try
        {
            var fromInstant = From is DateTime first ? LocalMidnight(first.Date) : (DateTimeOffset?)null;
            var toInstant = To is DateTime last ? LocalMidnight(last.Date.AddDays(1)) : (DateTimeOffset?)null;
            var loaded = new List<DprntLogLineInfo>();
            long afterId = 0;
            var hasMore = true;
            while (hasMore && loaded.Count < MaximumLines)
            {
                var page = await apiClient.GetDprntLogAsync(
                    choice.MachineId, fromInstant, toInstant, Search, afterId,
                    Math.Min(PageSize, MaximumLines - loaded.Count));
                loaded.AddRange(page.Lines);
                hasMore = page.HasMore && page.Lines.Count > 0;
                if (page.Lines.Count > 0) afterId = page.Lines[^1].Id;
            }
            foreach (var line in loaded) Lines.Add(line);
            loadedMachineLabel = choice.Label;
            Status = loaded.Count == 0
                ? $"{choice.Label}: no DPRNT lines in this period. The log holds every line received since the DPRNT log was installed."
                : hasMore
                    ? $"{choice.Label}: the first {loaded.Count:N0} lines are shown ({Range(loaded)}); more follow. Narrow the period or the search."
                    : $"{choice.Label}: {loaded.Count:N0} line(s), {Range(loaded)}.";
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            Status = Friendly(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Writes the loaded lines as a text file (time, tab, line) and opens it.</summary>
    internal Task ExportAsync()
    {
        if (Lines.Count == 0) return Task.CompletedTask;
        try
        {
            var text = new StringBuilder();
            foreach (var line in Lines) text.Append(line.TimeText).Append('\t').Append(line.Line).Append("\r\n");
            var folder = Path.Combine(Path.GetTempPath(), "MeimadPlanner", "Reports");
            Directory.CreateDirectory(folder);
            var name = string.Concat((loadedMachineLabel ?? "machine").Select(character =>
                Path.GetInvalidFileNameChars().Contains(character) || character == ' ' ? '_' : character));
            var path = Path.Combine(folder,
                $"dprnt-{name}-{Lines[0].ReceivedAt.ToLocalTime():yyyyMMdd-HHmmss}-{Lines[^1].ReceivedAt.ToLocalTime():yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(path, text.ToString(), new UTF8Encoding(true));
            openFile(path);
            Status = $"Exported {Lines.Count:N0} line(s): {path}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            Status = $"The log could not be exported: {exception.Message}";
        }
        return Task.CompletedTask;
    }

    private static string Range(IReadOnlyList<DprntLogLineInfo> lines) =>
        $"{lines[0].TimeText} - {lines[^1].TimeText}";

    private static DateTimeOffset LocalMidnight(DateTime date)
    {
        var local = DateTime.SpecifyKind(date, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    private static bool IsExpected(Exception exception) =>
        exception is PlannerApiException or PlannerProtocolException or HttpRequestException or TaskCanceledException;

    private static string Friendly(Exception exception) => exception switch
    {
        TaskCanceledException => "The Server did not respond before the client timeout.",
        HttpRequestException => "The configured Server could not be reached.",
        _ => exception.Message
    };

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
