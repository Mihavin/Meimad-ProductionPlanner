using System.IO;
using Microsoft.ClearScript.V8;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

/// <summary>
/// The TV Dashboard page (client-tv-dashboard/app.js) run in V8 with a minimal page stub: the part picture of a row is
/// fetched once per thumbnailVersion, the old picture and Part Number stay until the new picture has loaded and then
/// change together, rows without a picture show the placeholder, and an offline board says when its data is from
/// (owner request 2026-10-12).
/// </summary>
public sealed class TvDashboardPageTests : IDisposable
{
    // Just enough of a browser for app.js: elements by id, timers run by the test, pictures that load when told.
    private const string BrowserStub = """
        var elements = {};
        function element(id) {
          if (!elements[id]) {
            elements[id] = {
              id: id, innerHTML: "", textContent: "", className: "", title: "", style: {}, offsetHeight: 60, attributes: {},
              setAttribute: function (name, value) { this.attributes[name] = value; },
              getAttribute: function (name) { return this.attributes[name]; },
              removeAttribute: function (name) { delete this.attributes[name]; },
              addEventListener: function () {},
              classList: { toggle: function () {} }
            };
          }
          return elements[id];
        }
        var timers = [];
        function setTimeout(callback, delay) { timers.push({ callback: callback, delay: delay || 0 }); return timers.length; }
        function clearTimeout() {}
        // Runs the timers due now (delay 0) once; longer timers (clock, polling) stay queued.
        function runDueTimers() {
          var due = timers.filter(function (timer) { return timer.delay === 0; });
          timers = timers.filter(function (timer) { return timer.delay !== 0; });
          due.forEach(function (timer) { timer.callback(); });
        }
        var pictures = [];
        function Image() { pictures.push(this); }
        var document = {
          getElementById: element,
          querySelector: function () { return element("topbar"); },
          querySelectorAll: function () { return []; },
          documentElement: { lang: "", dir: "", style: {} },
          addEventListener: function () {}
        };
        var window = {
          location: { search: "?lang=en", protocol: "http:", host: "planner-server:5080", reload: function () {} },
          innerHeight: 1080, innerWidth: 1920, addEventListener: function () {},
          sessionStorage: { getItem: function () { return null; }, setItem: function () {} }
        };
        var navigator = { language: "en", languages: ["en"] };
        function fetch() { return new Promise(function () {}); }
        function WebSocket() {}
        WebSocket.OPEN = 1; WebSocket.CONNECTING = 0;
        function machine(id, part, op, version, extra) {
          var value = {
            machineId: id, number: id, name: "Mill " + id, connection: { online: true }, displayStatus: "RUNNING", warning: null,
            current: {
              partNumber: part, partName: "Bracket", batchNumber: "WO-1", operationNumber: op, operationName: "Mill " + op,
              thumbnailUrl: version ? "/api/v1/tv-dashboard/thumbnails/" + version : null, thumbnailVersion: version,
              thumbnailAlt: part + " - Bracket",
              progress: { statusCode: "started", phase: "production", completionPercent: 40, currentPart: 2, plannedParts: 5 }
            }
          };
          if (extra) Object.keys(extra).forEach(function (key) { value[key] = extra[key]; });
          return value;
        }
        function board(machines) { return { schemaVersion: 3, dashboardBuild: DASHBOARD_BUILD, refreshAfterSeconds: 15, machines: machines }; }
        function grid() { return element("machine-grid").innerHTML; }
        """;

    private readonly V8ScriptEngine engine = new();

    public TvDashboardPageTests()
    {
        engine.Execute(BrowserStub);
        engine.Execute(File.ReadAllText(Path.Combine(RepositoryRoot(), "client-tv-dashboard", "app.js")));
    }

    [Fact]
    public void Picture_is_loaded_once_per_version_and_changes_together_with_the_Part_Number()
    {
        engine.Execute("render(board([machine('M1', 'PN-100', 10, '1-aaa')]));");
        // No picture yet: the new part shows at once with the placeholder, and its picture starts loading.
        Assert.Contains("PN-100", Grid());
        Assert.Contains("thumb-placeholder", Grid());
        Assert.Equal(1, Number("pictures.length"));
        Assert.Equal("/api/v1/tv-dashboard/thumbnails/1-aaa", Text("pictures[0].src"));

        engine.Execute("pictures[0].onload(); runDueTimers();");
        Assert.Contains("<img src=\"/api/v1/tv-dashboard/thumbnails/1-aaa\"", Grid());
        Assert.Contains("alt=\"PN-100 - Bracket\"", Grid());

        // Status refreshes do not fetch the picture again.
        engine.Execute("render(board([machine('M1', 'PN-100', 10, '1-aaa')])); render(board([machine('M1', 'PN-100', 10, '1-aaa')]));");
        Assert.Equal(1, Number("pictures.length"));

        // The next Operation: the old picture and Part Number stay until its picture has loaded.
        engine.Execute("render(board([machine('M1', 'PN-200', 20, '1-bbb')]));");
        Assert.Equal(2, Number("pictures.length"));
        Assert.Contains("PN-100", Grid());
        Assert.Contains("thumbnails/1-aaa", Grid());
        Assert.DoesNotContain("PN-200", Grid());

        engine.Execute("pictures[1].onload(); runDueTimers();");
        Assert.Contains("PN-200", Grid());
        Assert.Contains("OP20", Grid());
        Assert.Contains("thumbnails/1-bbb", Grid());
        Assert.DoesNotContain("PN-100", Grid());
        Assert.DoesNotContain("thumbnails/1-aaa", Grid());
    }

    [Fact]
    public void A_picture_that_fails_gives_way_to_the_placeholder_and_a_row_without_one_swaps_at_once()
    {
        engine.Execute("render(board([machine('M1', 'PN-100', 10, '1-aaa')])); pictures[0].onload(); runDueTimers();");
        engine.Execute("render(board([machine('M1', 'PN-300', 30, '1-ccc')])); pictures[1].onerror(); runDueTimers();");
        Assert.Contains("PN-300", Grid());
        Assert.Contains("thumb-placeholder", Grid());
        Assert.Contains("<span class=\"thumb-part\">PN-300</span>", Grid());

        // No picture at all: the placeholder with the Part Number, at once, and nothing is fetched.
        engine.Execute("render(board([machine('M1', 'PN-400', 40, null)]));");
        Assert.Contains("<span class=\"thumb-part\">PN-400</span>", Grid());
        Assert.Equal(2, Number("pictures.length"));
    }

    [Fact]
    public void Errors_and_warnings_are_written_and_an_offline_board_says_when_its_data_is_from()
    {
        engine.Execute("""
            render(board([machine('M1', 'PN-100', 10, null, { displayStatus: 'ERROR', warning: { code: 'cnc_alarm', severity: 'error', label: 'CNC alarm' } }),
                          machine('M2', 'PN-200', 20, null, { current: null, displayStatus: 'IDLE' })]));
            """);
        Assert.Contains(">Error<span class=\"warning-sign warning-error\"", Grid());
        Assert.Contains("⚠</span> CNC alarm", Grid());
        Assert.Contains(">Idle<", Grid());
        Assert.Contains("No current operation", Grid());

        engine.Execute("state.snapshotAt = new Date(2026, 9, 12, 14, 32); setServerStatus('disconnected', t.disconnected);");
        Assert.Equal("Offline · data from 14:32", Text("element('server-status-text').textContent"));
    }

    [Fact]
    public void An_idle_row_shows_the_last_part_only_when_the_Server_sends_it()
    {
        engine.Execute("""
            render(board([machine('M1', 'PN-100', 10, null, { current: null, displayStatus: 'IDLE',
              idlePart: { partNumber: 'PN-LAST', partName: 'Cover', operationNumber: 30, operationName: 'Deburr',
                          thumbnailUrl: null, thumbnailVersion: null, thumbnailAlt: 'PN-LAST - Cover', thumbnailSource: 'placeholder' } })]));
            """);
        Assert.Contains("PN-LAST", Grid());
        Assert.Contains("Last part: OP30 Deburr", Grid());
        Assert.Contains(">Idle<", Grid());
    }

    public void Dispose() => engine.Dispose();

    private string Grid() => (string)engine.Evaluate("grid()");

    private string Text(string expression) => (string)engine.Evaluate(expression);

    private int Number(string expression) => Convert.ToInt32(engine.Evaluate(expression), System.Globalization.CultureInfo.InvariantCulture);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
