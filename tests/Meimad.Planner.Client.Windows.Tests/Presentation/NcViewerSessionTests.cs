using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Presentation.NcViewer;
using Meimad.Planner.NcEngine;
using Meimad.Planner.Client.Windows.Localization;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

public sealed class NcViewerSessionTests : IDisposable
{
    private const string MillProgram = "N10 G21 G90 G17 G54\nN20 T1 M6\nN30 G0 X0 Y0 Z100.\nN40 G0 X300.\nN50 G1 X360. F60.\nN60 G4 P2.\nN70 M30\n";
    private readonly string folder = Path.Combine(Path.GetTempPath(), "MeimadPlanner.NcViewer.Tests", Guid.NewGuid().ToString("N"));

    public NcViewerSessionTests() => Directory.CreateDirectory(folder);

    [Fact]
    public void Release_is_shown_read_only_and_its_packed_model_is_served_by_url() => SingleThread.Run(async () =>
    {
        var ui = new FakeUi();
        using var session = Session(Request(readOnly: true), ui);

        var initial = await ui.InvokeAsync(session, "getInitialState");
        Assert.Equal(MillProgram, initial.GetProperty("documentState").GetProperty("text").GetString());
        Assert.True(initial.GetProperty("meimad").GetProperty("readOnly").GetBoolean());
        Assert.Equal("HAAS_NGC", initial.GetProperty("meimad").GetProperty("dialect").GetString());

        ui.Send(session, "preview:message", new { type = "ready" });
        var render = await ui.NextEventAsync("preview:render");
        var url = render.GetProperty("modelUrl").GetString()!;
        Assert.StartsWith(NcViewerSession.ModelUrlPrefix, url, StringComparison.Ordinal);
        var model = Encoding.UTF8.GetString(ui.Models[url[NcViewerSession.ModelUrlPrefix.Length..]]);
        Assert.Contains("cnc-model-columns-1", model, StringComparison.Ordinal);
        Assert.Equal("haas-umc-500", (await ui.NextEventAsync("document:state")).GetProperty("machine").GetProperty("id").GetString());

        var save = await ui.InvokeRawAsync(session, "saveFile", MillProgram);
        Assert.False(save.GetProperty("ok").GetBoolean());
        Assert.Contains("immutable", save.GetProperty("error").GetString(), StringComparison.Ordinal);

        ui.Send(session, "document:update", new { text = "G0 X1\n", line = 1 });
        Assert.False(session.IsDirty);
    });

    [Fact]
    public void Applying_the_format_to_a_release_continues_on_an_unsaved_local_copy() => SingleThread.Run(async () =>
    {
        var ui = new FakeUi();
        (string Text, string Dialect)? received = null;
        var request = Request(readOnly: true) with
        {
            FormatService = (text, dialect, _) =>
            {
                received = (text, dialect);
                return Task.FromResult(new NcTemplateFormatResult(
                    "(PART: [[MEIMAD:PART_NAME]])\n" + text, dialect, true, ["Added identity header comments: PART."], [],
                    new NcTemplateValidation(false, "production_package_placeholder_required", "missing keys")));
            }
        };
        using var session = Session(request, ui);
        await ui.InvokeAsync(session, "getInitialState");

        var result = await ui.InvokeAsync(session, "meimadApplyFormat", "text the page sent", "FANUC_MACRO_B");

        Assert.Equal((MillProgram, "FANUC_MACRO_B"), received);
        Assert.True(result.GetProperty("becameCopy").GetBoolean());
        Assert.False(result.GetProperty("validation").GetProperty("isValid").GetBoolean());
        var state = await ui.NextEventAsync("document:state");
        Assert.StartsWith("(PART: [[MEIMAD:PART_NAME]])\n", state.GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.False((await ui.NextEventAsync("meimad:mode")).GetProperty("readOnly").GetBoolean());
        Assert.Equal("O1500-meimad.nc", session.DocumentName);
        Assert.True(session.IsDirty);
    });

    [Fact]
    public void Use_for_release_saves_the_program_and_hands_its_path_to_the_release_form() => SingleThread.Run(async () =>
    {
        var target = Path.Combine(folder, "O1500.nc");
        var ui = new FakeUi { SavePath = target };
        string? handedOver = null;
        var request = Request(readOnly: false) with
        {
            UseForRelease = path =>
            {
                handedOver = path;
                return Task.FromResult("selected");
            }
        };
        using var session = Session(request, ui);

        var result = await ui.InvokeAsync(session, "meimadUseForRelease", "O1500\nG0 X0\nM30\n");

        Assert.Equal(target, handedOver);
        Assert.Equal(target, result.GetProperty("path").GetString());
        Assert.Equal("O1500\r\nG0 X0\r\nM30\r\n", File.ReadAllText(target));
        Assert.False(session.IsDirty);
    });

    [Fact]
    public void A_release_can_be_edited_as_a_local_copy_and_saved_as_a_local_version() => SingleThread.Run(async () =>
    {
        var target = Path.Combine(folder, "O1500.nc");
        var ui = new FakeUi { SavePath = target };
        using var session = Session(Request(readOnly: true), ui);
        await ui.InvokeAsync(session, "getInitialState");

        await ui.InvokeAsync(session, "meimadEditCopy");

        Assert.False((await ui.NextEventAsync("meimad:mode")).GetProperty("readOnly").GetBoolean());
        Assert.True(session.IsDirty);
        Assert.Equal("O1500.nc", session.DocumentName);
        ui.Send(session, "document:update", new { text = MillProgram.Replace("X360.", "X420.", StringComparison.Ordinal), line = 5 });
        await ui.InvokeAsync(session, "saveFile", MillProgram.Replace("X360.", "X420.", StringComparison.Ordinal));

        Assert.Contains("X420.", File.ReadAllText(target), StringComparison.Ordinal);
        Assert.False(session.IsDirty);
    });

    [Fact]
    public void Release_to_server_is_refused_until_the_program_is_a_canonical_template() => SingleThread.Run(async () =>
    {
        var ui = new FakeUi { SavePath = Path.Combine(folder, "O1500.nc") };
        var released = false;
        var request = Request(readOnly: false) with
        {
            ValidateService = (_, _) => Task.FromResult(new NcTemplateValidation(false, "production_package_placeholder_required", "PART_NAME is missing")),
            ReleaseContext = ReleaseContext(),
            ReleaseToServer = (_, _) =>
            {
                released = true;
                return Task.FromResult(new NcViewerReleaseOutcome(true, "unexpected"));
            }
        };
        using var session = Session(request, ui);
        await ui.InvokeAsync(session, "getInitialState");

        var result = await ui.InvokeAsync(session, "meimadRelease", MillProgram, new { postprocessorId = "post-haas", releaseComment = "v2", confirmToolTable = true });

        Assert.False(result.GetProperty("released").GetBoolean());
        Assert.True(result.GetProperty("blocked").GetBoolean());
        Assert.Equal("production_package_placeholder_required", result.GetProperty("validation").GetProperty("code").GetString());
        Assert.False(released);
        Assert.False(File.Exists(Path.Combine(folder, "O1500.nc")));
    });

    [Fact]
    public void Release_to_server_checks_the_template_saves_the_program_and_hands_the_command_to_the_case() => SingleThread.Run(async () =>
    {
        var target = Path.Combine(folder, "O1500.nc");
        var ui = new FakeUi { SavePath = target };
        NcViewerReleaseCommand? received = null;
        var request = Request(readOnly: false) with
        {
            ValidateService = (_, _) => Task.FromResult(new NcTemplateValidation(true, null, null)),
            ReleaseContext = ReleaseContext(),
            ReleaseToServer = (command, _) =>
            {
                received = command;
                return Task.FromResult(new NcViewerReleaseOutcome(true, "Released O1500.nc: process r1, HAAS_4X post r2.", "release-2", 1, 2));
            }
        };
        using var session = Session(request, ui);
        var initial = await ui.InvokeAsync(session, "getInitialState");
        Assert.True(initial.GetProperty("meimad").GetProperty("canRelease").GetBoolean());
        Assert.Equal("post-haas", initial.GetProperty("meimad").GetProperty("release").GetProperty("defaultPostprocessorId").GetString());

        var edited = MillProgram.Replace("X360.", "X420.", StringComparison.Ordinal);
        var result = await ui.InvokeAsync(session, "meimadRelease", edited, new
        {
            postprocessorId = "post-haas",
            changeScope = "LOCAL_POST_REVISION",
            releaseComment = "Feed corrected",
            confirmToolTable = true
        });

        Assert.True(result.GetProperty("released").GetBoolean());
        Assert.Equal("release-2", result.GetProperty("releaseId").GetString());
        Assert.Equal(target, result.GetProperty("path").GetString());
        Assert.Contains("X420.", File.ReadAllText(target), StringComparison.Ordinal);
        Assert.NotNull(received);
        Assert.Equal(target, received!.FilePath);
        Assert.Equal("post-haas", received.PostprocessorId);
        Assert.Equal("LOCAL_POST_REVISION", received.ChangeScope);
        Assert.Equal("Feed corrected", received.ReleaseComment);
        Assert.True(received.ConfirmToolTable);
        Assert.True(received.HasActiveProcessRevision);
        Assert.False(session.IsDirty);
        Assert.Equal("Released O1500.nc: process r1, HAAS_4X post r2.", (await ui.NextEventAsync("app:status")).GetProperty("message").GetString());
    });

    private static NcViewerReleaseContext ReleaseContext() => new(
        "PN-1 · OP10 Mill",
        [new NcViewerReleaseTarget("post-haas", "HAAS_4X", "Current"), new NcViewerReleaseTarget("post-doosan", "Doosan 3X", "Missing — release required")],
        "post-haas",
        HasActiveProcessRevision: true,
        ToolTableFilePath: null);

    [Fact]
    public void Editing_reparses_and_marks_the_program_unsaved() => SingleThread.Run(async () =>
    {
        var ui = new FakeUi();
        using var session = Session(Request(readOnly: false), ui);
        await ui.InvokeAsync(session, "getInitialState");
        ui.Send(session, "preview:message", new { type = "ready" });
        await ui.NextEventAsync("preview:render");

        ui.Send(session, "document:update", new { text = MillProgram.Replace("X360.", "X420.", StringComparison.Ordinal), line = 5 });
        var render = await ui.NextEventAsync("preview:render");

        Assert.Equal(5, render.GetProperty("selectedLine").GetInt32());
        Assert.True(session.IsDirty);
        Assert.True(ui.LastTitleDirty);
    });

    [Fact]
    public void Page_localization_uses_the_client_catalog_and_translation_engine() => SingleThread.Run(async () =>
    {
        var ui = new FakeUi();
        using var session = Session(Request(readOnly: false), ui);
        var service = LocalizationService.Current;

        var payload = await ui.InvokeAsync(session, "meimadLocalization");
        var translated = await ui.InvokeAsync(session, "meimadTranslate", (object)new[] { "Ready.", "30P450045200-001" });

        Assert.Equal(service.CurrentLanguage, payload.GetProperty("language").GetString());
        Assert.Equal(service.IsRightToLeft, payload.GetProperty("rightToLeft").GetBoolean());
        Assert.Equal(service.CatalogEntryCount(service.CurrentLanguage), payload.GetProperty("entries").EnumerateObject().Count());
        Assert.Equal(
            new[] { service.Translate("Ready."), "30P450045200-001" },
            translated.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray());
    });

    [Fact]
    public void Standalone_ai_codex_and_step_features_are_refused() => SingleThread.Run(async () =>
    {
        var ui = new FakeUi();
        using var session = Session(Request(readOnly: false), ui);

        var result = await ui.InvokeRawAsync(session, "codexAsk", "explain");

        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Contains("not part of Meimad Planner", result.GetProperty("error").GetString(), StringComparison.Ordinal);
    });

    [Fact]
    public void Viewer_settings_are_validated_and_saved_on_this_pc() => SingleThread.Run(async () =>
    {
        var ui = new FakeUi();
        var store = new NcViewerSettingsStore(Path.Combine(folder, "settings.json"));
        using var session = new NcViewerSession(Request(readOnly: false), ui, store);

        await ui.InvokeAsync(session, "saveSettings", new
        {
            machine = "haas-umc-500",
            g30X = 200,
            g30Z = 90,
            initialVariables = "#500=1",
            programMemory = new Dictionary<string, string> { ["haas-umc-500"] = folder, ["unknown-machine"] = folder }
        });
        var saved = store.Load();
        Assert.Equal("haas-umc-500", saved.Machine);
        Assert.Equal(200d, saved.G30X);
        Assert.Equal([ "haas-umc-500" ], saved.ProgramMemory.Keys);

        var invalid = await ui.InvokeRawAsync(session, "saveSettings", new { machineParametersFile = Path.Combine(folder, "missing.txt") });
        Assert.False(invalid.GetProperty("ok").GetBoolean());
    });

    [Fact]
    public void Dialect_comes_from_the_machine_or_from_all_machines_of_the_postprocessor()
    {
        PlannerMachine Machine(string id, string dialect, params string[] posts) => new(
            id, id, id, "mill", null, [], "calendar", true, false, null, null, 0, 1,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, SupportedPostprocessorIds: posts, NcDialect: dialect);
        var machines = new[]
        {
            Machine("m10", "HAAS_NGC", "post-haas"),
            Machine("m14", "FANUC_MACRO_B", "post-fanuc", "post-shared"),
            Machine("m15", "HAAS_NGC", "post-shared")
        };

        Assert.Equal("FANUC_MACRO_B", NcViewerDialects.Resolve(machines, "m14", "post-haas"));
        Assert.Equal("HAAS_NGC", NcViewerDialects.Resolve(machines, null, "post-haas"));
        Assert.Null(NcViewerDialects.Resolve(machines, null, "post-shared"));
        Assert.Null(NcViewerDialects.Resolve(machines, null, null));
    }

    [Fact]
    public void Viewer_machine_comes_from_the_machine_setup_or_from_the_postprocessors_configured_machines()
    {
        PlannerMachine Machine(string id, string? viewer, params string[] posts) => new(
            id, id, id, "mill", null, [], "calendar", true, false, null, null, 0, 1,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, SupportedPostprocessorIds: posts, NcViewerMachine: viewer);
        var machines = new[]
        {
            Machine("m10", "haas-vf-3ss", "post-haas", "post-shared"),
            Machine("m14", null, "post-shared", "post-auto"),
            Machine("m15", "mazak-variaxis-i-500", "post-mazak", "post-mixed"),
            Machine("m16", "haas-umc-500", "post-mixed"),
            Machine("m17", "Not An Id", "post-bad")
        };

        Assert.Equal("haas-vf-3ss", NcViewerMachines.Resolve(machines, "m10", "post-mazak"));
        Assert.Null(NcViewerMachines.Resolve(machines, "m14", "post-haas"));
        // Machines on auto-detect do not disagree with a configured one.
        Assert.Equal("haas-vf-3ss", NcViewerMachines.Resolve(machines, null, "post-shared"));
        Assert.Null(NcViewerMachines.Resolve(machines, null, "post-auto"));
        Assert.Null(NcViewerMachines.Resolve(machines, null, "post-mixed"));
        Assert.Null(NcViewerMachines.Resolve(machines, null, "post-bad"));
        Assert.Null(NcViewerMachines.Resolve(machines, null, null));
        Assert.Null(NcViewerMachines.Normalize("auto"));
    }

    [Fact]
    public void Configured_viewer_machine_is_the_initial_machine_of_the_window() => SingleThread.Run(async () =>
    {
        var ui = new FakeUi();
        using var session = Session(Request(readOnly: true) with { MachineSelection = "haas-vf-3ss" }, ui);

        var initial = await ui.InvokeAsync(session, "getInitialState");
        Assert.Equal("haas-vf-3ss", initial.GetProperty("meimad").GetProperty("machineSelection").GetString());

        ui.Send(session, "preview:message", new { type = "ready" });
        await ui.NextEventAsync("preview:render");
        Assert.Equal("haas-vf-3ss", (await ui.NextEventAsync("document:state")).GetProperty("machine").GetProperty("id").GetString());

        // The viewer's own machine list still changes the open window.
        ui.Send(session, "preview:message", new { type = "machineChanged", machine = "haas-umc-500" });
        await ui.NextEventAsync("preview:render");
        Assert.Equal("haas-umc-500", (await ui.NextEventAsync("document:state")).GetProperty("machine").GetProperty("id").GetString());
    });

    public void Dispose()
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }

    private NcViewerSession Session(NcViewerOpenRequest request, FakeUi ui) =>
        new(request, ui, new NcViewerSettingsStore(Path.Combine(folder, "settings.json")));

    private static NcViewerOpenRequest Request(bool readOnly) => new(
        "PN-1 · OP10 Mill",
        "O1500.nc",
        NcViewerOpenRequest.NewDocument(MillProgram),
        readOnly,
        SourceDescription: "Server release: process r1",
        NcDialect: "HAAS_NGC");

    private sealed class FakeUi : INcViewerHostUi
    {
        private readonly ConcurrentQueue<JsonElement> posted = new();
        private readonly List<JsonElement> seen = [];
        private int nextId;

        public ConcurrentDictionary<string, byte[]> Models { get; } = new();
        public string? SavePath { get; init; }
        public bool LastTitleDirty { get; private set; }

        public void PostToPage(string json) => posted.Enqueue(JsonDocument.Parse(json).RootElement.Clone());
        public void PublishModel(string token, byte[] utf8Json) => Models[token] = utf8Json;
        public string? ToolTablePath { get; init; }
        public string? ChooseOpenFile(string? initialDirectory) => null;
        public string? ChooseSaveFile(string suggestedName, string? initialDirectory, string title) => SavePath;
        public string? ChooseFolder(string title, string? initialDirectory) => null;
        public string? ChooseToolTableFile(string? initialDirectory) => ToolTablePath;
        public bool ConfirmDiscardChanges(string documentName) => true;
        public void UpdateTitle(string documentName, bool dirty) => LastTitleDirty = dirty;

        public void Send(NcViewerSession session, string channel, object payload) =>
            session.HandleMessage(JsonSerializer.Serialize(new { kind = "send", channel, payload }));

        public async Task<JsonElement> InvokeAsync(NcViewerSession session, string method, params object[] args)
        {
            var result = await InvokeRawAsync(session, method, args);
            Assert.True(result.GetProperty("ok").GetBoolean(), result.TryGetProperty("error", out var error) ? error.GetString() : null);
            return result.GetProperty("value");
        }

        public async Task<JsonElement> InvokeRawAsync(NcViewerSession session, string method, params object[] args)
        {
            var id = ++nextId;
            session.HandleMessage(JsonSerializer.Serialize(new { kind = "invoke", id, method, args }));
            return await NextAsync(message => message.GetProperty("kind").GetString() == "result"
                && message.GetProperty("id").GetInt32() == id);
        }

        public async Task<JsonElement> NextEventAsync(string channel) =>
            (await NextAsync(message => message.GetProperty("kind").GetString() == "event"
                && message.GetProperty("channel").GetString() == channel)).GetProperty("payload");

        private async Task<JsonElement> NextAsync(Func<JsonElement, bool> match)
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                while (posted.TryDequeue(out var message)) seen.Add(message);
                var index = seen.FindIndex(message => match(message));
                if (index >= 0)
                {
                    var found = seen[index];
                    seen.RemoveAt(index);
                    return found;
                }
                await Task.Delay(10);
            }
            throw new TimeoutException("The NC viewer session did not post the expected message.");
        }
    }

    /// <summary>Runs async test code on one thread, as the WPF dispatcher runs the session.</summary>
    private sealed class SingleThread : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> work = new();

        public override void Post(SendOrPostCallback d, object? state)
        {
            // A background parse can finish after the test ended; run its continuation elsewhere.
            try
            {
                work.Add((d, state));
            }
            catch (InvalidOperationException)
            {
                ThreadPool.QueueUserWorkItem(_ => d(state));
            }
        }

        public static void Run(Func<Task> test)
        {
            var previous = Current;
            var context = new SingleThread();
            SetSynchronizationContext(context);
            try
            {
                var task = test();
                task.ContinueWith(_ => context.work.CompleteAdding(), TaskScheduler.Default);
                foreach (var (callback, state) in context.work.GetConsumingEnumerable()) callback(state);
                task.GetAwaiter().GetResult();
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }
    }
}
