using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Localization;
using Meimad.Planner.NcEngine;

namespace Meimad.Planner.Client.Windows.Presentation.NcViewer;

/// <summary>
/// The NC viewer's host logic: the role desktop/main.js plays in the upstream Electron viewer.
/// The page (meimad-bridge.js) calls methods and sends events as JSON web messages; this class
/// keeps the document, runs the NC engine on a background thread, and answers on the UI thread.
/// It never writes to the Server: releases stay immutable, and a formatted or edited program is
/// only a local file until the planner releases it through the normal Release G-code form.
/// A program of a Case Operation is saved in the Case Working Folder, in the revision folder of
/// the release it becomes (see <see cref="NcProgramFolders"/>).
/// </summary>
internal sealed class NcViewerSession : IDisposable
{
    internal const string ModelUrlPrefix = "https://ncviewer.meimad.local/__model/";
    private const int MaximumDocumentBytes = 32 * 1024 * 1024;
    private const int MaximumTranslationBatch = 500;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> UnsupportedMethods = new(StringComparer.Ordinal)
    {
        "openStepFile", "selectStepCylinder", "setStepZZero", "analyzeStepFace", "saveStepEnvelope",
        "revealStepEnvelope", "codexAsk", "codexCancel", "codexValidateApply", "chooseReferenceWorkspace",
        "clearReferenceWorkspace", "chooseCodexAttachments", "removeCodexAttachment", "clearCodexAttachments",
        "setApiKey", "clearApiKey"
    };

    private readonly NcViewerOpenRequest request;
    private readonly INcViewerHostUi ui;
    private readonly NcViewerSettingsStore settingsStore;
    private readonly Func<NcEngineRuntime> engineFactory;
    private readonly SemaphoreSlim engineGate = new(1, 1);
    private readonly Task<NcEngineRuntime> engineStartup;
    private NcEngineRuntime? engine;
    private NcViewerSettings settings;

    private string text;
    private string? filePath;
    private string documentName;
    private string lineEnding;
    private bool hasBom;
    private string encodingName;
    private bool dirty;
    private bool readOnly;
    private bool textPending = true;
    private int selectedLine = 1;
    private int? playbackLine;
    private string? machineOverride;
    private string? dialect;
    private JsonElement? toolTable;
    private string toolTableSource = "Program comments";
    private NcViewerToolRoomTable? toolRoomTable;
    private IReadOnlyList<NcEngineCompensationIssue> compensationIssues = [];
    private NcEngineMachineRef? activeMachine;
    private NcEngineEffectiveSettings? effectiveSettings;
    private bool parseRunning;
    private bool parseQueued;
    private bool synchronizeToolsQueued;
    private bool disposed;

    internal NcViewerSession(
        NcViewerOpenRequest request,
        INcViewerHostUi ui,
        NcViewerSettingsStore? settingsStore = null,
        Func<NcEngineRuntime>? engineFactory = null)
    {
        this.request = request;
        this.ui = ui;
        this.settingsStore = settingsStore ?? new NcViewerSettingsStore();
        this.engineFactory = engineFactory ?? (() => new NcEngineRuntime());
        settings = this.settingsStore.Load();
        text = NcTextFile.Normalize(request.Document.Text);
        lineEnding = request.Document.LineEnding;
        hasBom = request.Document.HasBom;
        encodingName = request.Document.EncodingName;
        filePath = request.FilePath;
        documentName = request.DocumentName;
        readOnly = request.ReadOnly;
        dialect = NcViewerDialects.Normalize(request.NcDialect);
        // The Meimad Machine's NC viewer machine (Setup) is the initial machine of this window;
        // the viewer's own machine list may change it. It is validated when the engine parses.
        machineOverride = NcViewerMachines.Normalize(request.MachineSelection);
        toolRoomTable = request.ToolRoomTable;
        // Loading the engine takes a moment; start it before the page asks for its first parse.
        engineStartup = Task.Run(StartEngine);
        LocalizationService.Current.LanguageChanged += OnLanguageChanged;
    }

    internal bool IsDirty => dirty && !readOnly;

    internal string DocumentName => documentName;

    /// <summary>Handles one web message from the page (UI thread).</summary>
    internal void HandleMessage(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return;
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("kind", out var kind)) return;
            switch (kind.GetString())
            {
                case "invoke":
                    var id = root.TryGetProperty("id", out var idValue) && idValue.TryGetInt64(out var number) ? number : 0;
                    var method = root.TryGetProperty("method", out var methodValue) ? methodValue.GetString() ?? string.Empty : string.Empty;
                    var arguments = root.TryGetProperty("args", out var argsValue) && argsValue.ValueKind == JsonValueKind.Array
                        ? argsValue.EnumerateArray().Select(value => value.Clone()).ToArray()
                        : [];
                    _ = InvokeAsync(id, method, arguments);
                    break;
                case "send":
                    var channel = root.TryGetProperty("channel", out var channelValue) ? channelValue.GetString() ?? string.Empty : string.Empty;
                    var payload = root.TryGetProperty("payload", out var payloadValue) ? payloadValue.Clone() : default;
                    HandleSend(channel, payload);
                    break;
            }
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        LocalizationService.Current.LanguageChanged -= OnLanguageChanged;
        _ = Task.Run(async () =>
        {
            await engineGate.WaitAsync();
            try
            {
                if (engineStartup.IsCompletedSuccessfully) engineStartup.Result.Dispose();
                engine?.Dispose();
            }
            finally
            {
                engineGate.Release();
            }
        });
    }

    // ----- invoke (request/response) ------------------------------------------------------------

    private async Task InvokeAsync(long id, string method, JsonElement[] args)
    {
        object? value;
        try
        {
            value = await DispatchAsync(method, args);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Post(new { kind = "result", id, ok = false, error = exception.Message });
            return;
        }
        Post(new { kind = "result", id, ok = true, value });
    }

    private async Task<object?> DispatchAsync(string method, JsonElement[] args)
    {
        if (UnsupportedMethods.Contains(method))
        {
            throw new InvalidOperationException("AI, Codex and STEP features of the standalone viewer are not part of Meimad Planner.");
        }
        switch (method)
        {
            case "getInitialState":
                await EnsureEngineAsync();
                var initial = new
                {
                    documentState = PublicDocumentState(includeText: true),
                    settings = await PublicSettingsAsync(),
                    meimad = MeimadState()
                };
                textPending = false;
                return initial;
            case "newFile":
                return NewDocument();
            case "openFile":
                return OpenDocument();
            case "saveFile":
                return await SaveDocumentAsync(Text(args, 0), saveAs: false);
            case "saveFileAs":
                return await SaveDocumentAsync(Text(args, 0), saveAs: true);
            case "getSettings":
            case "openSettings":
                return await PublicSettingsAsync();
            case "saveSettings":
                return await SaveSettingsAsync(args.Length > 0 ? args[0] : default);
            case "chooseProgramMemoryFolder":
                var folder = ui.ChooseFolder("Machine program memory folder", null);
                return folder is null ? new { canceled = true, path = (string?)null } : new { canceled = false, path = (string?)folder };
            case "reloadXml":
                ScheduleParse(synchronizeTools: true);
                return PublicDocumentState(includeText: false);
            case "getToolTable":
                return await EditableToolTableAsync();
            case "saveToolTable":
                return await SaveToolTableAsync(args.Length > 0 ? args[0] : default);
            case "showToolTable":
                return new { shown = false };
            case "meimadState":
                return MeimadState();
            case "meimadLocalization":
                return LocalizationPayload();
            case "meimadTranslate":
                return TranslateTexts(args.Length > 0 ? args[0] : default);
            case "meimadApplyFormat":
                return await ApplyMeimadFormatAsync(Text(args, 0), args.Length > 1 ? args[1].GetString() : null);
            case "meimadUseForRelease":
                return await UseForReleaseAsync(Text(args, 0));
            case "meimadValidate":
                return await ValidateTemplateAsync(Text(args, 0));
            case "meimadEditCopy":
                return EditCopy();
            case "meimadRelease":
                return await ReleaseToServerAsync(Text(args, 0), args.Length > 1 ? args[1] : default);
            case "meimadChooseToolTable":
                var toolTableFile = ui.ChooseToolTableFile(request.ReleaseContext?.ToolTableFilePath is { } known ? Path.GetDirectoryName(known) : null);
                return toolTableFile is null ? new { canceled = true, path = (string?)null } : new { canceled = false, path = (string?)toolTableFile };
            case "meimadStock":
                return await StockStateAsync();
            case "meimadStockSave":
                return await StockSaveAsync(args.Length > 0 ? args[0] : default);
            case "meimadChooseStl":
                var stlFile = ui.ChooseStlFile(await StockDialogFolderAsync());
                return stlFile is null ? new { canceled = true, path = (string?)null } : new { canceled = false, path = (string?)stlFile };
            case "meimadReadStl":
                return StockReadStl(Text(args, 0));
            case "meimadExportStl":
                return await StockExportStlAsync(Text(args, 0), args.Length > 1 ? args[1] : default);
            default:
                throw new InvalidOperationException($"The NC viewer host has no '{method}' method.");
        }
    }

    private object NewDocument()
    {
        if (dirty && !readOnly && !ui.ConfirmDiscardChanges(documentName)) return new { canceled = true };
        ReplaceDocument(NcViewerOpenRequest.BlankProgram, null, "Untitled.NC", "\r\n", false, "UTF-8", dirty: false);
        return PublicDocumentState(includeText: true);
    }

    private object OpenDocument()
    {
        if (dirty && !readOnly && !ui.ConfirmDiscardChanges(documentName)) return new { canceled = true };
        var path = ui.ChooseOpenFile(filePath is null ? null : Path.GetDirectoryName(filePath));
        if (path is null) return new { canceled = true };
        var info = new FileInfo(path);
        if (info.Length > MaximumDocumentBytes)
        {
            throw new InvalidOperationException($"{info.Name} is larger than {MaximumDocumentBytes / (1024 * 1024)} MB.");
        }
        var loaded = NcTextFile.Decode(File.ReadAllBytes(path));
        ReplaceDocument(loaded.Text, path, info.Name, loaded.LineEnding, loaded.HasBom, loaded.EncodingName, dirty: false);
        return PublicDocumentState(includeText: true);
    }

    private async Task<object> SaveDocumentAsync(string editorText, bool saveAs)
    {
        if (readOnly)
        {
            if (!saveAs)
            {
                throw new InvalidOperationException("This is an immutable Server release. Use \"Save copy as\" to keep a local copy.");
            }
            // A copy of a release starts in that release's own revision folder.
            var releaseFolder = await SourceReleaseFolderAsync();
            var copyPath = ui.ChooseSaveFile(documentName, releaseFolder ?? DefaultProgramFolder(), "Save a local copy of the release");
            if (copyPath is null) return new { canceled = true };
            WriteDocument(copyPath, text);
            SendStatus($"Copy saved to {copyPath}. The Server release is unchanged.", "success");
            return PublicDocumentState(includeText: false);
        }

        text = NcTextFile.Normalize(editorText);
        string? target;
        if (request.ProgramFolders is not null)
        {
            // A modified program of a Case Operation goes to the revision folder of the release it
            // becomes; "Save as" opens that folder. When the folder cannot be resolved the status
            // bar says why and a file dialog keeps the edits from being lost.
            var folder = await NextReleaseFolderAsync(null, null);
            if (folder is null || saveAs)
            {
                target = ui.ChooseSaveFile(documentName, folder?.Path ?? DefaultProgramFolder(), "Save NC program");
            }
            else
            {
                target = Path.Combine(folder.Path, documentName);
                if (!ConfirmTarget(target)) return new { canceled = true };
            }
        }
        else
        {
            target = saveAs || filePath is null
                ? ui.ChooseSaveFile(documentName, filePath is null ? DefaultProgramFolder() : Path.GetDirectoryName(filePath), "Save NC program")
                : filePath;
        }
        if (target is null) return new { canceled = true };
        WriteDocument(target, text);
        filePath = target;
        documentName = Path.GetFileName(target);
        dirty = false;
        SendMeimadMode();
        ui.UpdateTitle(documentName, dirty);
        return PublicDocumentState(includeText: false);
    }

    private async Task<object> SaveSettingsAsync(JsonElement partial)
    {
        if (partial.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Invalid settings.");
        var runtime = await EnsureEngineAsync();
        var machines = runtime.Machines;
        var next = settings.Clone();
        if (partial.TryGetProperty("machine", out var machine))
        {
            var id = machine.GetString() ?? NcEngineInfo.AutoMachine;
            next.Machine = id == NcEngineInfo.AutoMachine || machines.Any(value => value.Id == id) ? id : NcEngineInfo.AutoMachine;
        }
        next.G30X = FiniteOrNull(partial, "g30X") ?? next.G30X;
        next.G30Z = FiniteOrNull(partial, "g30Z") ?? next.G30Z;
        if (partial.TryGetProperty("initialVariables", out var variables))
            next.InitialVariables = Limit(variables.GetString());
        if (partial.TryGetProperty("machineParametersFile", out var parameters))
            next.MachineParametersFile = Limit(parameters.GetString()).Trim();
        if (partial.TryGetProperty("toolTableFile", out var toolFile))
            next.ToolTableFile = Limit(toolFile.GetString()).Trim();
        if (partial.TryGetProperty("programMemory", out var memory) && memory.ValueKind == JsonValueKind.Object)
        {
            next.ProgramMemory = memory.EnumerateObject()
                .Where(entry => machines.Any(value => value.Id == entry.Name) && entry.Value.ValueKind == JsonValueKind.String)
                .Select(entry => (entry.Name, Folder: Limit(entry.Value.GetString()).Trim()))
                .Where(entry => entry.Folder.Length > 0)
                .ToDictionary(entry => entry.Name, entry => entry.Folder, StringComparer.Ordinal);
        }
        if (!string.IsNullOrWhiteSpace(next.MachineParametersFile) && !File.Exists(next.MachineParametersFile))
        {
            throw new InvalidOperationException($"CNC parameter file not found: {next.MachineParametersFile}");
        }
        if (!string.IsNullOrWhiteSpace(next.ToolTableFile) && !File.Exists(next.ToolTableFile))
        {
            throw new InvalidOperationException($"Tool table file not found: {next.ToolTableFile}");
        }
        settings = next;
        settingsStore.Save(settings);
        ScheduleParse(synchronizeTools: true);
        return await PublicSettingsAsync();
    }

    private async Task<object> EditableToolTableAsync()
    {
        var snapshot = CreateSnapshot();
        var (inferred, source) = await WithEngineAsync(runtime =>
        {
            runtime.SetReadableFolders(snapshot.ReadableFolders);
            var table = runtime.InferToolTable(snapshot.Text, snapshot.DocumentName, snapshot.MachineSelection,
                snapshot.Dialect, snapshot.ToolTable ?? FallbackToolTable(runtime, snapshot));
            // The page may ask for the table before the first preview: the Tool Room values apply here too.
            return snapshot.ToolTable is null && snapshot.ToolRoomTable is { } toolRoom
                ? (ApplyToolRoomTable(runtime, snapshot, table, toolRoom, null), (string?)toolRoom.Source)
                : (table, (string?)null);
        });
        toolTable = inferred.Table;
        if (source is not null) toolTableSource = source;
        return WithToolTableSource(inferred.Editable);
    }

    private async Task<object> SaveToolTableAsync(JsonElement edited)
    {
        if (edited.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Invalid tool table payload.");
        var snapshot = CreateSnapshot();
        var current = toolTable ?? (await WithEngineAsync(runtime => runtime.InferToolTable(
            snapshot.Text, snapshot.DocumentName, snapshot.MachineSelection, snapshot.Dialect, null))).Table;
        var saved = await WithEngineAsync(runtime => runtime.SaveToolTable(
            snapshot.Text, snapshot.DocumentName, snapshot.MachineSelection, snapshot.Dialect, current, edited));
        toolTable = saved.Table;
        toolTableSource = "Manual edits (this viewer window only)";
        ScheduleParse();
        SendStatus("Tool values updated for this viewer window. They are not stored on the Server.", "success");
        return new { table = WithToolTableSource(saved.Editable), documentState = PublicDocumentState(includeText: false) };
    }

    private async Task<object> ApplyMeimadFormatAsync(string editorText, string? requestedDialect)
    {
        if (request.FormatService is null)
        {
            throw new InvalidOperationException("Connect to the Meimad Server to apply the Meimad Planner format.");
        }
        var formatDialect = NcViewerDialects.Normalize(requestedDialect) ?? dialect ?? NcViewerDialects.Default;
        var source = readOnly ? text : NcTextFile.Normalize(editorText);
        var result = await request.FormatService(source, formatDialect, CancellationToken.None);
        var wasReadOnly = readOnly;
        if (wasReadOnly)
        {
            // The release itself never changes: continue on an unsaved local copy.
            readOnly = false;
            filePath = null;
            documentName = FormattedCopyName(documentName);
        }
        dialect = formatDialect;
        text = NcTextFile.Normalize(result.Text);
        dirty = dirty || wasReadOnly || result.Changed;
        textPending = true;
        SendDocumentState();
        SendMeimadMode();
        ScheduleParse(synchronizeTools: true);
        var outcome = result.Validation.IsValid
            ? "The program is a valid Meimad canonical template."
            : $"The program is not yet a valid Meimad template: {result.Validation.Message}";
        SendStatus(outcome, result.Validation.IsValid ? "success" : "warning");
        return new
        {
            changed = result.Changed,
            changes = result.Changes,
            warnings = result.Warnings,
            validation = result.Validation,
            ncDialect = result.NcDialect,
            becameCopy = wasReadOnly
        };
    }

    private async Task<object> UseForReleaseAsync(string editorText)
    {
        if (request.UseForRelease is null) throw new InvalidOperationException("This viewer was not opened from a G-code release form.");
        if (!readOnly) text = NcTextFile.Normalize(editorText);
        string? path;
        if (request.ProgramFolders is not null)
        {
            // Saved in the revision folder of the release the Release G-code form creates. When the
            // form releases it with another postprocessor or scope, the file moves to that folder.
            NcProgramFolder folder;
            try
            {
                folder = await request.ProgramFolders.NextReleaseAsync(null, null);
            }
            catch (Exception exception) when (IsFolderFailure(exception))
            {
                throw new InvalidOperationException(FolderFailure(exception), exception);
            }
            path = Path.Combine(folder.Path, readOnly ? FormattedCopyName(documentName) : documentName);
            if (!ConfirmTarget(path)) return new { canceled = true };
            SaveReleasedCopy(path);
        }
        else
        {
            path = filePath;
            if (path is null || dirty || readOnly)
            {
                path = ui.ChooseSaveFile(readOnly ? FormattedCopyName(documentName) : documentName,
                    filePath is null ? DefaultProgramFolder() : Path.GetDirectoryName(filePath),
                    "Save the NC program to release");
                if (path is null) return new { canceled = true };
                SaveReleasedCopy(path);
            }
        }
        var message = await request.UseForRelease(path);
        SendStatus(message, "success");
        return new { canceled = false, path, message };
    }

    private async Task<object> ValidateTemplateAsync(string editorText)
    {
        if (request.ValidateService is null)
        {
            throw new InvalidOperationException("Connect to the Meimad Server to check the Meimad canonical format.");
        }
        var source = readOnly ? text : NcTextFile.Normalize(editorText);
        var validation = await request.ValidateService(source, CancellationToken.None);
        return new { validation };
    }

    /// <summary>
    /// "Edit copy": the immutable release continues as an unsaved local copy with the same name,
    /// which can be saved locally (a local version) or released as a new revision.
    /// </summary>
    private object EditCopy()
    {
        if (readOnly)
        {
            readOnly = false;
            filePath = null;
            dirty = true;
            SendMeimadMode();
            SendDocumentState();
            SendStatus("Editing a local copy of the release. The Server release is unchanged; save the copy or release it as a new revision.", "success");
        }
        return PublicDocumentState(includeText: false);
    }

    /// <summary>
    /// "Release to Server": the Server's canonical-template check first (nothing is uploaded when
    /// it fails), then the program is saved, then the Case Operation releases the saved file with
    /// the same rules as the Release G-code form.
    /// </summary>
    private async Task<object> ReleaseToServerAsync(string editorText, JsonElement options)
    {
        if (request.ReleaseToServer is null || request.ReleaseContext is null)
        {
            throw new InvalidOperationException("This viewer was not opened from a Case Operation; release the program from the Cases tab.");
        }
        if (options.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Release details are required.");
        if (!readOnly) text = NcTextFile.Normalize(editorText);

        if (request.ValidateService is not null)
        {
            var validation = await request.ValidateService(text, CancellationToken.None);
            if (!validation.IsValid)
            {
                SendStatus($"Not released: {validation.Message}", "warning");
                return new
                {
                    released = false,
                    blocked = true,
                    validation,
                    message = $"The program is not a valid Meimad canonical template: {validation.Message} Apply the Meimad Planner format, then release again."
                };
            }
        }

        var postprocessorId = OptionText(options, "postprocessorId") ?? request.ReleaseContext.DefaultPostprocessorId ?? string.Empty;
        var changeScope = OptionText(options, "changeScope")
            ?? (request.ReleaseContext.HasActiveProcessRevision ? NcProgramFolders.LocalPostRevision : NcProgramFolders.NewProcessRevision);
        string? path;
        if (request.ProgramFolders is not null)
        {
            // The released program must be in the Case Working Folder: it is saved in the folder of
            // the release it becomes before anything is uploaded.
            NcProgramFolder folder;
            try
            {
                folder = await request.ProgramFolders.NextReleaseAsync(
                    string.IsNullOrEmpty(postprocessorId) ? null : postprocessorId, changeScope);
            }
            catch (Exception exception) when (IsFolderFailure(exception))
            {
                var reason = FolderFailure(exception);
                SendStatus($"Not released: {reason}", "error");
                return new { released = false, message = $"Not released: {reason}" };
            }
            path = Path.Combine(folder.Path, readOnly ? FormattedCopyName(documentName) : documentName);
            if (filePath is null || !NcProgramFolders.SamePath(path, filePath) || dirty || readOnly)
            {
                if (!ConfirmTarget(path)) return new { released = false, canceled = true };
                SaveReleasedCopy(path);
            }
        }
        else
        {
            path = filePath;
            if (path is null || dirty || readOnly)
            {
                path = ui.ChooseSaveFile(readOnly ? FormattedCopyName(documentName) : documentName,
                    filePath is null ? DefaultProgramFolder() : Path.GetDirectoryName(filePath),
                    "Save the NC program to release");
                if (path is null) return new { released = false, canceled = true };
                SaveReleasedCopy(path);
            }
        }

        var command = new NcViewerReleaseCommand(
            path,
            postprocessorId,
            changeScope,
            OptionText(options, "releaseComment") ?? string.Empty,
            OptionText(options, "processChangeDescription"),
            OptionFlag(options, "confirmNewProcessRevision"),
            OptionFlag(options, "reuseActiveToolTable"),
            OptionFlag(options, "confirmToolTable"),
            OptionText(options, "toolTableFilePath"),
            request.ReleaseContext.HasActiveProcessRevision);
        var outcome = await request.ReleaseToServer(command, CancellationToken.None);
        if (outcome.FilePath is { } releasedPath && !NcProgramFolders.SamePath(releasedPath, path))
        {
            // The Server assigned other numbers than expected: the program moved to their folder.
            path = releasedPath;
            filePath = releasedPath;
            documentName = Path.GetFileName(releasedPath);
            SendDocumentState();
        }
        SendStatus(outcome.Message, outcome.Succeeded ? "success" : "error");
        return new
        {
            released = outcome.Succeeded,
            message = outcome.Message,
            releaseId = outcome.ReleaseId,
            processRevisionNumber = outcome.ProcessRevisionNumber,
            postSpecificRevision = outcome.PostSpecificRevision,
            path
        };
    }

    /// <summary>Writes the program the release uses and makes it the saved document.</summary>
    private void SaveReleasedCopy(string path)
    {
        WriteDocument(path, text);
        filePath = path;
        documentName = Path.GetFileName(path);
        readOnly = false;
        dirty = false;
        SendDocumentState();
        SendMeimadMode();
    }

    /// <summary>
    /// The revision folder of the release the program becomes, or null when it cannot be resolved
    /// (offline, no Case Working Folder, the folder cannot be created); the status bar says why.
    /// </summary>
    private async Task<NcProgramFolder?> NextReleaseFolderAsync(string? postprocessorId, string? changeScope)
    {
        if (request.ProgramFolders is null) return null;
        try
        {
            return await request.ProgramFolders.NextReleaseAsync(postprocessorId, changeScope);
        }
        catch (Exception exception) when (IsFolderFailure(exception))
        {
            SendStatus(FolderFailure(exception), "warning");
            return null;
        }
    }

    /// <summary>The revision folder of the release this document shows, when it comes from one.</summary>
    private async Task<string?> SourceReleaseFolderAsync()
    {
        if (request.ProgramFolders?.Source is not { } source) return null;
        try
        {
            return (await request.ProgramFolders.ReleaseFolderAsync(source)).Path;
        }
        catch (Exception exception) when (IsFolderFailure(exception))
        {
            SendStatus(FolderFailure(exception), "warning");
            return null;
        }
    }

    /// <summary>
    /// A revision folder may already hold a file of the same name. This document's own file and a
    /// file with identical content are replaced silently; any other file only after asking.
    /// </summary>
    private bool ConfirmTarget(string target)
    {
        if (!File.Exists(target)) return true;
        if (filePath is not null && NcProgramFolders.SamePath(target, filePath)) return true;
        try
        {
            var bytes = NcTextFile.Encode(text, lineEnding, hasBom, encodingName);
            if (File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes)) return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Unreadable: ask like for any other file.
        }
        return ui.ConfirmReplaceFile(target);
    }

    private static bool IsFolderFailure(Exception exception) => exception is InvalidOperationException
        or IOException or UnauthorizedAccessException or HttpRequestException or TaskCanceledException
        or PlannerApiException or NotSupportedException;

    private static string FolderFailure(Exception exception) => exception is InvalidOperationException
        ? exception.Message
        : $"The Case G-code folder is unavailable: {exception.Message}";

    private static string? OptionText(JsonElement options, string name)
    {
        if (!options.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return null;
        var trimmed = value.GetString()?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static bool OptionFlag(JsonElement options, string name) =>
        options.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    // ----- send (fire and forget) ---------------------------------------------------------------

    private void HandleSend(string channel, JsonElement payload)
    {
        switch (channel)
        {
            case "document:update":
                if (readOnly || payload.ValueKind != JsonValueKind.Object) return;
                text = NcTextFile.Normalize(payload.TryGetProperty("text", out var value) ? value.GetString() ?? string.Empty : string.Empty);
                if (payload.TryGetProperty("line", out var line) && line.TryGetInt32(out var number)) selectedLine = Math.Max(1, number);
                dirty = true;
                ui.UpdateTitle(documentName, dirty);
                ScheduleParse();
                break;
            case "document:dirty":
                if (readOnly) return;
                dirty = true;
                ui.UpdateTitle(documentName, dirty);
                break;
            case "document:selection":
                if (payload.ValueKind == JsonValueKind.Number && payload.TryGetInt32(out var selection)) selectedLine = Math.Max(1, selection);
                break;
            case "preview:message":
                HandlePreviewMessage(payload);
                break;
        }
    }

    private void HandlePreviewMessage(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("type", out var typeValue)) return;
        switch (typeValue.GetString())
        {
            case "ready":
                ScheduleParse(synchronizeTools: true);
                break;
            case "g30Changed":
                var x = FiniteOrNull(message, "x");
                var z = FiniteOrNull(message, "z");
                if (x is null || z is null) return;
                settings.G30X = x;
                settings.G30Z = z;
                PersistSettings();
                ScheduleParse();
                break;
            case "macrosChanged":
                settings.InitialVariables = Limit(message.TryGetProperty("value", out var macros) ? macros.GetString() : string.Empty);
                PersistSettings();
                ScheduleParse();
                break;
            case "reloadParameters":
                ScheduleParse(synchronizeTools: true);
                SendStatus("Machine and tool data reloaded.", "success");
                break;
            case "workOffsetsChanged":
                _ = SaveWorkOffsetsAsync(message);
                break;
            case "machineChanged":
                var requested = message.TryGetProperty("machine", out var machine) ? machine.GetString() : null;
                machineOverride = string.IsNullOrWhiteSpace(requested) || requested == NcEngineInfo.AutoMachine ? null : requested;
                toolTable = null;
                ScheduleParse(synchronizeTools: true);
                break;
            case "selectLine":
                if (!message.TryGetProperty("line", out var lineValue) || !lineValue.TryGetInt32(out var selected)) return;
                selectedLine = Math.Max(1, selected);
                SendEvent("editor:selection", new { line = selectedLine });
                SendEvent("preview:selection", new { type = "selection", line = selectedLine });
                break;
            case "playbackLine":
                playbackLine = message.TryGetProperty("line", out var playback) && playback.TryGetInt32(out var playbackNumber)
                    ? Math.Max(1, playbackNumber)
                    : null;
                SendDecorations();
                break;
        }
    }

    private async Task SaveWorkOffsetsAsync(JsonElement message)
    {
        try
        {
            var runtime = await EnsureEngineAsync();
            var machineId = message.TryGetProperty("machine", out var machine) ? machine.GetString() : null;
            var definition = runtime.Machines.FirstOrDefault(value => value.Id == machineId);
            if (definition is null || definition.Type != "mill")
            {
                SendStatus("Home offsets can only be saved for a mill.", "error");
                return;
            }
            var offsets = message.TryGetProperty("offsets", out var value) ? value.Clone() : default;
            var all = settings.WorkOffsets is { ValueKind: JsonValueKind.Object } existing
                ? JsonNode.Parse(existing.GetRawText())!.AsObject()
                : new JsonObject();
            all[definition.Id] = offsets.ValueKind == JsonValueKind.Undefined ? null : JsonNode.Parse(offsets.GetRawText());
            var clean = await WithEngineAsync(engineRuntime =>
                engineRuntime.SanitizeWorkOffsets(JsonSerializer.SerializeToElement(all)));
            settings.WorkOffsets = clean;
            PersistSettings();
            ScheduleParse();
            SendStatus($"{definition.Name} home offsets saved on this PC.", "success");
        }
        catch (Exception exception) when (exception is NcEngineException or IOException or UnauthorizedAccessException
            or JsonException or InvalidOperationException)
        {
            SendStatus($"Home offsets were not saved: {exception.Message}", "error");
        }
    }

    // ----- parsing ------------------------------------------------------------------------------

    private void ScheduleParse(bool synchronizeTools = false)
    {
        if (disposed) return;
        parseQueued = true;
        synchronizeToolsQueued |= synchronizeTools;
        if (parseRunning) return;
        parseRunning = true;
        _ = RunParsesAsync();
    }

    private async Task RunParsesAsync()
    {
        try
        {
            while (parseQueued && !disposed)
            {
                parseQueued = false;
                var synchronize = synchronizeToolsQueued || toolTable is null;
                synchronizeToolsQueued = false;
                var snapshot = CreateSnapshot();
                ParseOutcome outcome;
                try
                {
                    outcome = await WithEngineAsync(runtime => Parse(runtime, snapshot, synchronize));
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    // Includes an engine that could not start (missing files or native V8 library):
                    // the editor keeps working and the status bar says why the preview is missing.
                    SendStatus($"Preview could not be updated: {exception.Message}", "error");
                    continue;
                }
                // A newer edit is waiting: drop this result and parse the newer text.
                if (parseQueued || disposed) continue;
                ApplyOutcome(outcome);
            }
        }
        finally
        {
            parseRunning = false;
        }
    }

    private static ParseOutcome Parse(NcEngineRuntime runtime, ParseSnapshot snapshot, bool synchronizeTools)
    {
        runtime.SetReadableFolders(snapshot.ReadableFolders);
        var table = snapshot.ToolTable;
        var source = snapshot.ToolTableSource;
        var warnings = new List<string>();
        if (synchronizeTools || table is null)
        {
            var baseTable = snapshot.ToolTable ?? FallbackToolTable(runtime, snapshot, warnings);
            var inferred = runtime.InferToolTable(snapshot.Text, snapshot.DocumentName, snapshot.MachineSelection,
                snapshot.Dialect, baseTable);
            table = inferred.Table;
            // The first inference of a program opened with the Tool Room's table takes its measured values.
            if (snapshot.ToolTable is null && snapshot.ToolRoomTable is { } toolRoom)
            {
                table = ApplyToolRoomTable(runtime, snapshot, inferred, toolRoom, warnings).Table;
                source = toolRoom.Source;
            }
        }
        string? parameterText = null;
        string? parameterPath = null;
        if (!string.IsNullOrWhiteSpace(snapshot.Settings.MachineParametersFile))
        {
            if (File.Exists(snapshot.Settings.MachineParametersFile))
            {
                parameterPath = snapshot.Settings.MachineParametersFile;
                parameterText = File.ReadAllText(parameterPath);
            }
            else
            {
                warnings.Add($"Configured CNC parameter file was not found: {snapshot.Settings.MachineParametersFile}; the bundled parameters are used.");
            }
        }
        var result = runtime.ParsePreview(new NcEnginePreviewRequest(
            snapshot.Text,
            snapshot.MachineSelection,
            snapshot.Dialect,
            new NcEngineSettings(snapshot.Settings.G30X, snapshot.Settings.G30Z, snapshot.Settings.InitialVariables),
            table,
            source,
            snapshot.Settings.WorkOffsets,
            snapshot.Settings.ProgramMemory,
            snapshot.DocumentDirectory,
            parameterText,
            parameterPath,
            warnings));
        return new ParseOutcome(result, table, source);
    }

    private void ApplyOutcome(ParseOutcome outcome)
    {
        var summary = outcome.Result.Summary;
        toolTable = outcome.ToolTable;
        if (outcome.ToolTableSource is { } tableSource) toolTableSource = tableSource;
        compensationIssues = summary.CompensationIssues;
        activeMachine = summary.Machine;
        effectiveSettings = summary.Settings;
        playbackLine = null;
        var token = Guid.NewGuid().ToString("N");
        ui.PublishModel(token, Encoding.UTF8.GetBytes(outcome.Result.Packed));
        SendEvent("preview:render", new
        {
            type = "render",
            name = documentName,
            modelUrl = ModelUrlPrefix + token,
            settings = summary.Settings,
            selectedLine
        });
        SendDocumentState();
        SendDecorations();
        if (machineOverride is not null && summary.Machine is { } active && active.Id != machineOverride)
        {
            // The configured NC viewer machine is not installed with this client's engine.
            SendStatus($"NC viewer machine '{machineOverride}' is not installed with this client; the preview uses {active.Name} ({summary.SelectionReason ?? "detected"}).", "warning");
            return;
        }
        SendStatus($"Preview updated: {summary.SegmentCount:N0} segments.", "success");
    }

    private ParseSnapshot CreateSnapshot()
    {
        var directory = filePath is null ? null : Path.GetDirectoryName(filePath);
        var selection = machineOverride ?? (string.IsNullOrWhiteSpace(settings.Machine) ? NcEngineInfo.AutoMachine : settings.Machine);
        var readable = new List<string?> { directory };
        readable.AddRange(settings.ProgramMemory.Values);
        return new ParseSnapshot(
            text,
            documentName,
            selection,
            selection == NcEngineInfo.AutoMachine ? dialect : null,
            settings.Clone(),
            toolTable,
            toolTableSource,
            directory,
            readable,
            toolRoomTable);
    }

    private static JsonElement? FallbackToolTable(NcEngineRuntime runtime, ParseSnapshot snapshot, List<string>? warnings = null)
    {
        var path = snapshot.Settings.ToolTableFile;
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (!File.Exists(path))
        {
            warnings?.Add($"Fallback tool table was not found: {path}");
            return null;
        }
        try
        {
            return runtime.ParseToolTableXml(File.ReadAllText(path));
        }
        catch (NcEngineException exception)
        {
            warnings?.Add($"Could not load {path}: {exception.Message}");
            return null;
        }
    }

    /// <summary>
    /// Writes the Tool Room's measured values and cutter shapes into the inferred table through
    /// the engine's own validation. Tools the program does not use are ignored; a failure keeps
    /// the inferred table and says why.
    /// </summary>
    private static NcEngineToolTable ApplyToolRoomTable(
        NcEngineRuntime runtime, ParseSnapshot snapshot, NcEngineToolTable inferred, NcViewerToolRoomTable toolRoom, List<string>? warnings)
    {
        try
        {
            var edited = toolRoom.ApplyTo(inferred.Editable, out var unmatched);
            warnings?.AddRange(unmatched);
            return runtime.SaveToolTable(snapshot.Text, snapshot.DocumentName, snapshot.MachineSelection,
                snapshot.Dialect, inferred.Table, JsonSerializer.SerializeToElement(edited));
        }
        catch (NcEngineException exception)
        {
            warnings?.Add($"{toolRoom.Source} could not be applied: {exception.Message}");
            return inferred;
        }
    }

    // ----- stock and machined-stock hand-over (meimad-simulation.js) ---------------------------

    private const long MaximumStlBytes = 200L * 1024 * 1024;

    /// <summary>The stock definition travels with the program: <c>{program}.stock.json</c> in the program's folder.</summary>
    private async Task<string?> StockSidecarPathAsync()
    {
        var folder = filePath is not null ? Path.GetDirectoryName(filePath) : readOnly ? await SourceReleaseFolderAsync() : null;
        return folder is null ? null : Path.Combine(folder, documentName + ".stock.json");
    }

    /// <summary>Where STL dialogs start: this Operation's Stock folder when it exists, else the program's folder.</summary>
    private async Task<string?> StockDialogFolderAsync()
    {
        if (request.ProgramFolders is not null)
        {
            try
            {
                var folder = await request.ProgramFolders.StockFolderAsync(nextOperation: false, create: false);
                if (Directory.Exists(folder)) return folder;
            }
            catch (Exception exception) when (IsStockFolderFailure(exception))
            {
                // Fall through to the program's own folder.
            }
        }
        return filePath is null ? DefaultProgramFolder() : Path.GetDirectoryName(filePath);
    }

    private async Task<object> StockStateAsync()
    {
        var sidecar = await StockSidecarPathAsync();
        JsonElement? stock = null;
        if (sidecar is not null && File.Exists(sidecar))
        {
            try
            {
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(sidecar));
                if (document.RootElement.ValueKind == JsonValueKind.Object) stock = document.RootElement.Clone();
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
            {
                SendStatus($"The stock definition could not be read: {sidecar}: {exception.Message}", "warning");
            }
        }
        var candidates = new List<object>();
        int? nextOperation = null;
        if (request.ProgramFolders is not null)
        {
            try
            {
                var stockFolder = await request.ProgramFolders.StockFolderAsync(nextOperation: false, create: false);
                if (Directory.Exists(stockFolder))
                {
                    candidates.AddRange(Directory.EnumerateFiles(stockFolder, "*.stl")
                        .Order(StringComparer.OrdinalIgnoreCase)
                        .Select(path => new { name = Path.GetFileName(path), path }));
                }
                nextOperation = await request.ProgramFolders.NextOperationNumberAsync();
            }
            catch (Exception exception) when (IsStockFolderFailure(exception))
            {
                // No Working Folder or no Server: the stock stays in this window.
            }
        }
        return new { stock, candidates, canSave = sidecar is not null, savePath = sidecar, nextOperationNumber = nextOperation };
    }

    private async Task<object> StockSaveAsync(JsonElement stock)
    {
        if (stock.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Invalid stock definition.");
        var json = stock.GetRawText();
        if (json.Length > 65536) throw new InvalidOperationException("The stock definition is too large to save.");
        var sidecar = await StockSidecarPathAsync()
            ?? throw new InvalidOperationException("The program has no folder yet: save it in the Case Working Folder, then save the stock with it.");
        Directory.CreateDirectory(Path.GetDirectoryName(sidecar)!);
        await File.WriteAllTextAsync(sidecar, json);
        return new { path = sidecar };
    }

    private static object StockReadStl(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new InvalidOperationException($"STL file not found: {path}");
        if (!string.Equals(Path.GetExtension(path), ".stl", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Choose an .stl file.");
        if (new FileInfo(path).Length > MaximumStlBytes) throw new InvalidOperationException("The STL file is larger than 200 MB.");
        var bytes = File.ReadAllBytes(path);
        return new { name = Path.GetFileName(path), base64 = Convert.ToBase64String(bytes), size = bytes.Length };
    }

    /// <summary>
    /// Writes the machined stock: into the next Operation's Stock folder in the Case Working
    /// Folder (<c>{program}-machined.stl</c>, replacing an older export only after asking), or to a
    /// file the user chooses.
    /// </summary>
    private async Task<object> StockExportStlAsync(string base64, JsonElement options)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("Invalid STL payload.");
        }
        if (bytes.Length < 84) throw new InvalidOperationException("The machined stock is empty; run the material removal first.");
        var toNextOperation = options.ValueKind == JsonValueKind.Object
            && options.TryGetProperty("toNextOperation", out var flag) && flag.ValueKind == JsonValueKind.True;
        var name = Path.GetFileNameWithoutExtension(documentName) + "-machined.stl";
        string? target;
        if (toNextOperation)
        {
            if (request.ProgramFolders is null)
                throw new InvalidOperationException("This viewer was not opened from a Case Operation; use \"Save STL as\" instead.");
            var folder = await request.ProgramFolders.StockFolderAsync(nextOperation: true, create: true);
            target = Path.Combine(folder, name);
            if (File.Exists(target) && !ui.ConfirmReplaceFile(target)) return new { canceled = true, path = (string?)null };
        }
        else
        {
            target = ui.ChooseSaveFile(name, await StockDialogFolderAsync(), "Save the machined stock (STL)");
            if (target is null) return new { canceled = true, path = (string?)null };
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllBytesAsync(target, bytes);
        return new { canceled = false, path = (string?)target, size = bytes.Length };
    }

    private static bool IsStockFolderFailure(Exception exception) =>
        IsFolderFailure(exception) || exception is HttpRequestException or TaskCanceledException or PlannerApiException or NotSupportedException;

    // ----- engine -------------------------------------------------------------------------------

    private NcEngineRuntime StartEngine() => engineFactory();

    private async Task<NcEngineRuntime> EnsureEngineAsync()
    {
        await engineGate.WaitAsync();
        try
        {
            return CurrentEngine(await engineStartup);
        }
        finally
        {
            engineGate.Release();
        }
    }

    private NcEngineRuntime CurrentEngine(NcEngineRuntime started)
    {
        if (engine is null) engine = started;
        if (engine.IsFaulted)
        {
            engine.Dispose();
            engine = engineFactory();
        }
        return engine;
    }

    private async Task<T> WithEngineAsync<T>(Func<NcEngineRuntime, T> action)
    {
        var started = await engineStartup;
        await engineGate.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var runtime = CurrentEngine(started);
            return await Task.Run(() => action(runtime));
        }
        finally
        {
            engineGate.Release();
        }
    }

    // ----- page state ---------------------------------------------------------------------------

    private object PublicDocumentState(bool includeText)
    {
        var state = new Dictionary<string, object?>
        {
            ["name"] = documentName,
            // The page shows this in its status bar; a release has no local path.
            ["filePath"] = filePath ?? (readOnly ? request.SourceDescription : null),
            ["eol"] = lineEnding switch { "\n" => "LF", "\r" => "CR", _ => "CRLF" },
            ["encoding"] = hasBom ? "UTF-8 with BOM" : encodingName,
            ["dirty"] = dirty && !readOnly,
            ["selectedLine"] = selectedLine,
            ["toolTablePath"] = toolTableSource,
            ["machineParameterPath"] = string.IsNullOrWhiteSpace(settings.MachineParametersFile) ? "Bundled CNC-PARA.TXT" : settings.MachineParametersFile,
            ["machine"] = activeMachine is null
                ? null
                : new
                {
                    id = activeMachine.Id,
                    name = activeMachine.Name,
                    type = activeMachine.Type,
                    programMemory = settings.ProgramMemory.GetValueOrDefault(activeMachine.Id)
                }
        };
        if (includeText)
        {
            state["text"] = text;
            textPending = false;
        }
        return state;
    }

    private async Task<object> PublicSettingsAsync()
    {
        var runtime = await EnsureEngineAsync();
        return new
        {
            machine = settings.Machine,
            g30X = effectiveSettings?.G30X ?? settings.G30X ?? 250,
            g30Z = effectiveSettings?.G30Z ?? settings.G30Z ?? 100,
            initialVariables = settings.InitialVariables,
            machineParametersFile = settings.MachineParametersFile,
            toolTableFile = settings.ToolTableFile,
            programMemory = settings.ProgramMemory,
            workOffsets = settings.WorkOffsets,
            referenceWorkspace = string.Empty,
            aiToolRecognitionEnabled = false,
            aiModel = string.Empty,
            aiDebounceMs = 1800,
            keyConfigured = false,
            machines = runtime.Machines.Select(machine => new
            {
                id = machine.Id,
                name = machine.Name,
                type = machine.Type,
                control = machine.Control,
                builtIn = machine.BuiltIn
            }),
            activeMachine = activeMachine is null ? null : new { id = activeMachine.Id, name = activeMachine.Name, type = activeMachine.Type }
        };
    }

    private object MeimadState() => new
    {
        readOnly,
        source = request.SourceDescription,
        context = request.ContextTitle,
        canFormat = request.FormatService is not null,
        canUseForRelease = request.UseForRelease is not null,
        savesToCaseFolder = request.ProgramFolders is not null,
        dialect = dialect ?? NcViewerDialects.Default,
        dialectKnown = dialect is not null,
        dialects = NcViewerDialects.All.Select(option => new { id = option.Id, name = option.Name }),
        machineSelection = request.MachineSelection,
        canValidate = request.ValidateService is not null,
        canRelease = request.ReleaseToServer is not null && request.ReleaseContext is not null,
        release = request.ReleaseContext is null
            ? null
            : new
            {
                operation = request.ReleaseContext.OperationTitle,
                postprocessors = request.ReleaseContext.Postprocessors.Select(target => new { id = target.Id, name = target.Name, status = target.Status }),
                defaultPostprocessorId = request.ReleaseContext.DefaultPostprocessorId,
                hasActiveProcessRevision = request.ReleaseContext.HasActiveProcessRevision,
                toolTableFilePath = request.ReleaseContext.ToolTableFilePath
            }
    };

    private object WithToolTableSource(JsonElement editable)
    {
        var node = JsonNode.Parse(editable.GetRawText())!.AsObject();
        node["sourcePath"] = toolTableSource;
        node["saved"] = false;
        return node;
    }

    private void ReplaceDocument(string newText, string? path, string name, string eol, bool bom, string encoding, bool dirty)
    {
        text = NcTextFile.Normalize(newText);
        filePath = path;
        documentName = name;
        lineEnding = eol;
        hasBom = bom;
        encodingName = encoding;
        this.dirty = dirty;
        readOnly = false;
        selectedLine = 1;
        playbackLine = null;
        toolTable = null;
        toolTableSource = "Program comments";
        toolRoomTable = null; // another program: the Tool Room's table belongs to the opened release
        textPending = true;
        machineOverride = null;
        SendMeimadMode();
        ui.UpdateTitle(documentName, dirty);
        ScheduleParse(synchronizeTools: true);
    }

    private void WriteDocument(string path, string content)
    {
        var bytes = NcTextFile.Encode(content, lineEnding, hasBom, encodingName);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private void SendDocumentState()
    {
        SendEvent("document:state", PublicDocumentState(includeText: textPending));
        ui.UpdateTitle(documentName, dirty && !readOnly);
    }

    private void SendMeimadMode() => SendEvent("meimad:mode", MeimadState());

    // The page is outside the WPF tree, so it translates its own text: the catalog of the
    // current language for exact matches, and the host engine for composed messages.
    internal static object LocalizationPayload()
    {
        var service = LocalizationService.Current;
        var language = service.CurrentLanguage;
        return new
        {
            language,
            rightToLeft = service.IsRightToLeft,
            entries = service.CatalogEntries(language)
        };
    }

    internal static IReadOnlyList<string> TranslateTexts(JsonElement texts)
    {
        if (texts.ValueKind != JsonValueKind.Array) return [];
        var service = LocalizationService.Current;
        return texts.EnumerateArray()
            .Take(MaximumTranslationBatch)
            .Select(item => item.ValueKind == JsonValueKind.String ? service.Translate(item.GetString() ?? string.Empty) : string.Empty)
            .ToArray();
    }

    private void OnLanguageChanged(object? sender, EventArgs e) =>
        SendEvent("meimad:localization", LocalizationPayload());

    private void SendDecorations() => SendEvent("editor:decorations", new
    {
        errorLines = compensationIssues.Select(issue => new { line = issue.Line, message = issue.Message }),
        playbackLine
    });

    private void SendStatus(string message, string level) =>
        SendEvent("app:status", new { message, level });

    private void SendEvent(string channel, object payload) =>
        Post(new { kind = "event", channel, payload });

    private void Post(object message)
    {
        if (disposed) return;
        ui.PostToPage(JsonSerializer.Serialize(message, Json));
    }

    private void PersistSettings()
    {
        try
        {
            settingsStore.Save(settings);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SendStatus($"Viewer settings could not be saved: {exception.Message}", "warning");
        }
    }

    internal static string DefaultProgramFolder()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Meimad Planner", "NC programs");
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }
        return folder;
    }

    internal static string FormattedCopyName(string name)
    {
        var extension = Path.GetExtension(name);
        var stem = Path.GetFileNameWithoutExtension(name);
        return stem.EndsWith("-meimad", StringComparison.OrdinalIgnoreCase)
            ? name
            : $"{stem}-meimad{(string.IsNullOrEmpty(extension) ? ".nc" : extension)}";
    }

    private static string Text(JsonElement[] args, int index) =>
        args.Length > index && args[index].ValueKind == JsonValueKind.String ? args[index].GetString() ?? string.Empty : string.Empty;

    private static double? FiniteOrNull(JsonElement value, string property) =>
        value.TryGetProperty(property, out var number) && number.ValueKind == JsonValueKind.Number
            && number.TryGetDouble(out var result) && double.IsFinite(result)
            ? result
            : null;

    private static string Limit(string? value)
    {
        var text = value ?? string.Empty;
        return text.Length <= NcViewerSettings.MaximumTextLength ? text : text[..NcViewerSettings.MaximumTextLength];
    }

    private sealed record ParseSnapshot(
        string Text,
        string DocumentName,
        string MachineSelection,
        string? Dialect,
        NcViewerSettings Settings,
        JsonElement? ToolTable,
        string ToolTableSource,
        string? DocumentDirectory,
        IReadOnlyList<string?> ReadableFolders,
        NcViewerToolRoomTable? ToolRoomTable);

    private sealed record ParseOutcome(NcEnginePreviewResult Result, JsonElement? ToolTable, string? ToolTableSource);
}
