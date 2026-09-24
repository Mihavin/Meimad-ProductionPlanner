using System.Text.Json;
using Microsoft.ClearScript;
using Microsoft.ClearScript.V8;

namespace Meimad.Planner.NcEngine;

public sealed record NcEngineRuntimeOptions
{
    /// <summary>Folder holding src/, media/, machines/, controls/, meimad/ (default: next to the app).</summary>
    public string? EngineRoot { get; init; }

    /// <summary>Soft V8 heap limit; exceeding it fails the call instead of the process.</summary>
    public int MaximumHeapMegabytes { get; init; } = 1536;

    /// <summary>Longest a single analysis or parse may run before it is interrupted.</summary>
    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromSeconds(120);
}

/// <summary>
/// One V8 engine with the vendored Chevalier NC engine loaded. Calls are serialized; the engine
/// is not reentrant. After a timeout or heap-limit failure the instance is <see cref="IsFaulted"/>
/// and must be disposed and replaced.
/// </summary>
public sealed class NcEngineRuntime : IDisposable
{
    private static readonly string[] ModuleFolders = ["src", "media", "meimad"];
    private readonly object gate = new();
    private readonly V8ScriptEngine engine;
    private readonly NcEngineFileHost fileHost;
    private readonly ScriptObject adapter;
    private readonly TimeSpan callTimeout;
    private bool disposed;

    public NcEngineRuntime(NcEngineRuntimeOptions? options = null)
    {
        options ??= new NcEngineRuntimeOptions();
        EngineRoot = Path.GetFullPath(options.EngineRoot ?? DefaultEngineRoot);
        var bootstrapPath = Path.Combine(EngineRoot, "meimad", "bootstrap.js");
        var adapterPath = Path.Combine(EngineRoot, "meimad", "meimad-nc-engine.js");
        if (!File.Exists(bootstrapPath) || !File.Exists(adapterPath))
        {
            throw new NcEngineException($"The NC engine files were not found in '{EngineRoot}'.");
        }

        callTimeout = options.CallTimeout;
        fileHost = new NcEngineFileHost(EngineRoot);
        var heapBytes = (ulong)Math.Max(256, options.MaximumHeapMegabytes) * 1024 * 1024;
        engine = new V8ScriptEngine(
            "MeimadNcEngine",
            new V8RuntimeConstraints { MaxOldSpaceSize = (int)(heapBytes / (1024 * 1024)) + 512 },
            V8ScriptEngineFlags.None);
        try
        {
            engine.MaxRuntimeHeapSize = (UIntPtr)heapBytes;
            engine.RuntimeHeapSizeSampleInterval = TimeSpan.FromMilliseconds(100);
            engine.RuntimeHeapSizeViolationPolicy = V8RuntimeViolationPolicy.Exception;
            engine.AddHostObject("__meimadHost", fileHost);
            engine.Execute(new DocumentInfo(new Uri(bootstrapPath)), File.ReadAllText(bootstrapPath));
            var modules = (ScriptObject)engine.Script.__meimadModules;
            foreach (var folder in ModuleFolders)
            {
                var directory = Path.Combine(EngineRoot, folder);
                if (!Directory.Exists(directory)) continue;
                foreach (var file in Directory.EnumerateFiles(directory, "*.js").Order(StringComparer.OrdinalIgnoreCase))
                {
                    if (string.Equals(file, bootstrapPath, StringComparison.OrdinalIgnoreCase)) continue;
                    // Keep the source on the wrapper's first line so script line numbers match the file.
                    var factory = engine.Evaluate(
                        new DocumentInfo(new Uri(file)),
                        "(function (exports, require, module, __filename, __dirname) {" + File.ReadAllText(file) + "\n})");
                    modules.InvokeMethod("register", file, factory);
                }
            }
            adapter = (ScriptObject)modules.InvokeMethod("require", adapterPath);
            Machines = Deserialize<IReadOnlyList<NcEngineMachineSummary>>(Call("machines"));
        }
        catch
        {
            engine.Dispose();
            throw;
        }
    }

    public static string DefaultEngineRoot => Path.Combine(AppContext.BaseDirectory, "nc-engine");

    public string EngineRoot { get; }

    public bool IsFaulted { get; private set; }

    public IReadOnlyList<NcEngineMachineSummary> Machines { get; }

    /// <summary>Folders (besides the engine) that subprogram lookup may read on this call.</summary>
    public void SetReadableFolders(IEnumerable<string?> folders) => fileHost.SetAdditionalRoots(folders);

    public NcEngineAnalysis Analyze(NcEngineAnalysisRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var json = JsonSerializer.Serialize(new
        {
            text = NcPlaceholderText.ForEngine(request.Text),
            dialect = request.Dialect,
            machineSelection = request.MachineSelection,
            settings = SettingsJson(request.Settings),
            documentDirectory = request.DocumentDirectory,
            programMemory = request.ProgramMemory,
            machineParametersText = BundledMachineParameters()
        }, NcEngineInfo.Json);
        return Deserialize<NcEngineAnalysis>(Call("analyze", json, cancellationToken));
    }

    public NcEnginePreviewResult ParsePreview(NcEnginePreviewRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var json = JsonSerializer.Serialize(new
        {
            text = NcPlaceholderText.ForEngine(request.Text),
            machineSelection = request.MachineSelection,
            dialect = request.Dialect,
            settings = SettingsJson(request.Settings),
            toolTable = request.ToolTable,
            toolTableSourcePath = request.ToolTableSourcePath,
            workOffsets = request.WorkOffsets,
            programMemory = request.ProgramMemory,
            documentDirectory = request.DocumentDirectory,
            machineParametersText = request.MachineParametersText ?? BundledMachineParameters(),
            machineParameterPath = request.MachineParameterPath ?? Path.Combine(EngineRoot, "CNC-PARA.TXT"),
            resourceWarnings = request.ResourceWarnings
        }, NcEngineInfo.Json);
        lock (gate)
        {
            var summary = Deserialize<NcEnginePreviewSummary>(Call("parsePreview", json, cancellationToken));
            var packed = Call("takePacked");
            return new NcEnginePreviewResult(packed, summary);
        }
    }

    public NcEngineToolTable InferToolTable(string text, string documentName, string machineSelection,
        string? dialect, JsonElement? existingTable)
    {
        var json = JsonSerializer.Serialize(new
        {
            text = NcPlaceholderText.ForEngine(text),
            documentName,
            machineSelection,
            dialect,
            toolTable = existingTable
        }, NcEngineInfo.Json);
        return Deserialize<NcEngineToolTable>(Call("toolTable", json));
    }

    public NcEngineToolTable SaveToolTable(string text, string documentName, string machineSelection,
        string? dialect, JsonElement currentTable, JsonElement edited)
    {
        var json = JsonSerializer.Serialize(new
        {
            text = NcPlaceholderText.ForEngine(text),
            documentName,
            machineSelection,
            dialect,
            toolTable = currentTable,
            edited
        }, NcEngineInfo.Json);
        return Deserialize<NcEngineToolTable>(Call("saveToolTable", json));
    }

    /// <summary>Reads a tool table XML file into the engine's table object.</summary>
    public JsonElement ParseToolTableXml(string xml) =>
        Deserialize<JsonElement>(Call("parseToolTableXml", xml ?? string.Empty));

    /// <summary>Validated home offsets by machine id (drops unknown machines and axes).</summary>
    public JsonElement SanitizeWorkOffsets(JsonElement offsets) =>
        Deserialize<JsonElement>(Call("sanitizeOffsets", offsets.GetRawText()));

    public string BundledMachineParameters()
    {
        var path = Path.Combine(EngineRoot, "CNC-PARA.TXT");
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            engine.Dispose();
        }
    }

    private static object? SettingsJson(NcEngineSettings? settings) => settings is null
        ? null
        : new { g30X = settings.G30X, g30Z = settings.G30Z, initialVariables = settings.InitialVariables };

    /// <summary>Evaluates a script expression under the same guards as engine calls (tests only).</summary>
    internal object? EvaluateForTesting(string code, CancellationToken cancellationToken = default) =>
        Guarded(() => engine.Evaluate(code), cancellationToken);

    /// <summary>
    /// The program as the interpreter reads it (dialect translation, inlined lathe subprograms)
    /// with its line map, as JSON (tests and diagnostics).
    /// </summary>
    internal JsonElement PrepareForTesting(NcEnginePreviewRequest request)
    {
        var json = JsonSerializer.Serialize(new
        {
            text = NcPlaceholderText.ForEngine(request.Text),
            machineSelection = request.MachineSelection,
            dialect = request.Dialect,
            programMemory = request.ProgramMemory,
            documentDirectory = request.DocumentDirectory
        }, NcEngineInfo.Json);
        return Deserialize<JsonElement>(Call("prepare", json));
    }

    private string Call(string method, string? argument = null, CancellationToken cancellationToken = default) =>
        Guarded(() => argument is null ? adapter.InvokeMethod(method) : adapter.InvokeMethod(method, argument),
            cancellationToken) as string ?? string.Empty;

    private object? Guarded(Func<object?> action, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (IsFaulted) throw new NcEngineException("The NC engine must be restarted after a previous failure.");
            cancellationToken.ThrowIfCancellationRequested();
            var timedOut = false;
            using var timer = new Timer(_ =>
            {
                timedOut = true;
                engine.Interrupt();
            }, null, callTimeout, Timeout.InfiniteTimeSpan);
            using var registration = cancellationToken.Register(() => engine.Interrupt());
            try
            {
                return action();
            }
            catch (ScriptInterruptedException exception)
            {
                IsFaulted = true;
                cancellationToken.ThrowIfCancellationRequested();
                if (timedOut) throw new NcEngineTimeoutException(callTimeout);
                throw new NcEngineException("The NC engine was interrupted.", exception);
            }
            catch (ScriptEngineException exception)
            {
                // A heap-limit violation leaves the engine's state undefined; replace it.
                if (exception.IsFatal || exception.Message.Contains("heap", StringComparison.OrdinalIgnoreCase))
                {
                    IsFaulted = true;
                }
                throw new NcEngineException(ScriptMessage(exception), exception);
            }
        }
    }

    private static string ScriptMessage(ScriptEngineException exception)
    {
        var message = exception.Message;
        const string prefix = "Error: ";
        return message.StartsWith(prefix, StringComparison.Ordinal) ? message[prefix.Length..] : message;
    }

    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, NcEngineInfo.Json)
        ?? throw new NcEngineException("The NC engine returned no result.");
}
