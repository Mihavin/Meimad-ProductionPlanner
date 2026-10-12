using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Meimad.CimatronExporter;

/// <summary>
/// <see cref="ICimatronApi"/> over a running Cimatron through late-bound COM, so no Cimatron interop
/// assembly is needed to build or ship the exporter. Adapter version <see cref="Version"/>: it connects
/// through <c>CimAppAccess.AppAccess</c> (exporter definition §3.1), takes the active document and its NC
/// model, and finds each API method by name on the model, the document and the application in that
/// order. A member is called through the vtable interfaces of Cimatron's installed interop assemblies
/// when they have it (<see cref="CimatronBinder"/>), else through IDispatch. Capability detection looks
/// the member names up before anything is called, so a Cimatron version without a method reports it as
/// unavailable instead of failing half-way.
/// The names in <see cref="Members"/> and the argument lists are those of the Cimatron 2026.0 type libraries
/// (<c>cimatron-api.txt</c> of the Meimad probe, 2026-10-12): procedures and UCS names come from
/// <c>GetProcessManagerAsXML</c> (fallback: the <c>ModelProcedures</c> entity list and an entity query for UCS
/// entities), the UCS matrix from <c>IUcs.Transformation</c>, the remaining stock from
/// <c>ExportProceduresRemainingStock(procedure, NcUcsType, file)</c>. Cimatron 2026.0 has no API that writes the
/// initial stock, the part or the fixtures as STL: those stay fallbacks (<c>--stock-file</c>, a stock STL found beside
/// the document, <c>--geometry-folder</c>).
/// UNVERIFIED: this adapter has not yet completed an export against a real Cimatron — run <c>--probe</c> first.
/// </summary>
internal sealed class CimatronComApi : ICimatronApi
{
    internal const string Version = "cimatron-com-6";
    internal const string AppAccessProgId = "CimAppAccess.AppAccess";

    /// <summary>How the last <see cref="Connect"/> reached Cimatron (null: the registered ProgID).</summary>
    internal static string? ConnectedThrough { get; private set; }

    /// <summary>The vtable binder over Cimatron's interop assemblies (null: IDispatch only).</summary>
    internal static CimatronBinder? Binder { get; private set; }

    /// <summary>Problems loading Cimatron's interop assemblies, for the probe.</summary>
    internal static List<string> BinderProblems { get; } = [];

    /// <summary>What the last <see cref="Connect"/> tried to reach the Cimatron application, for the probe.</summary>
    internal static List<string> ConnectTrace { get; } = [];

    /// <summary>The late-bound member names, in one place so a field test can adjust them.</summary>
    internal static class Members
    {
        internal static readonly string[] GetApplication = ["GetActiveApplication", "GetApplication"];
        internal const string GetActiveDoc = "GetActiveDoc";
        internal const string Model = "Model";
        internal const string ProcessManagerXml = "GetProcessManagerAsXML";
        internal const string PostByUcs = "PostByUcsToAllNcProcedures";
        internal const string PostToAll = "ExecutePostToAllNcProcedures";
        internal const string PostSelected = "PostByUcsByIdVec";
        internal const string CuttersCsv = "ExportCuttersAndHoldersToCSV";
        internal const string JobsXml = "NcExportJobsData";
        internal const string RemainingStock = "ExportProceduresRemainingStock";
        internal const string ModelProcedures = "ModelProcedures";
        internal const string ActiveUcs = "ActivateUcs";
        internal const string CreateFilter = "CreateFilter";
        internal const string SetFilter = "SetFilter";
        internal const string SelectEntities = "Select";
        internal const string FilterAdd = "Add";
        internal const string ActiveToModel = "Active2ModelTranslate";
        internal const string Transformation = "Transformation";
        internal static readonly string[] Matrix = ["Matrix", "Tranformation"];
        internal const string Vector = "Vector";
        // Cimatron 2026.0 exposes none of these; the lists are kept so a later version can be matched by name.
        internal static readonly string[] InitialStock = [];
        internal static readonly string[] EntityExport = [];
        internal static readonly string[] MultiStl = [];
        internal static readonly string[] Tools = [];
        internal static readonly string[] Version = ["Version", "GetVersion"];
        internal static readonly string[] Name = ["Title", "Name", "GetName"];
        internal static readonly string[] Path = ["GetPath", "Path", "FullName"];
        internal static readonly string[] EntityId = ["ID", "Id", "GetId"];

        /// <summary><c>EFilterEnumType.cmFilterEntityType</c>: the kind of filter <c>CreateFilter</c> makes (an IFilterType).</summary>
        internal const int EntityTypeFilter = 3;

        /// <summary><c>EntityEnumType.cmUcs</c>, added to the entity-type filter with <c>IFilterType.Add</c>.</summary>
        internal const int UcsEntityType = 9;

        /// <summary><c>NcUcsType.cmNcProcUcs</c> and <c>cmNcModelUcs</c>.</summary>
        internal const int ProcedureUcsType = 1;
        internal const int ModelUcsType = 3;
    }

    private readonly object application;
    private readonly object? document;
    private readonly object? model;
    private ProcessManagerData? processManager;

    private CimatronComApi(object application, object? document, object? model)
    {
        this.application = application;
        this.document = document;
        this.model = model;
        CimatronVersion = TryGetString(application, Members.Version);
        var ucsQuery = model is not null && HasMember(model, Members.CreateFilter) && HasMember(model, Members.SetFilter) && HasMember(model, Members.SelectEntities);
        var activeUcs = model is not null && HasMember(model, Members.ActiveUcs);
        Capabilities = new CimatronCapabilities(
            PostByUcs: Find(Members.PostByUcs) is not null,
            PostToAll: Find(Members.PostToAll) is not null,
            PostSelected: Find(Members.PostSelected) is not null,
            CuttersAndHoldersCsv: Find(Members.CuttersCsv) is not null,
            JobsXml: Find(Members.JobsXml) is not null,
            RemainingStock: Find(Members.RemainingStock) is not null,
            // Cimatron 2026.0 has none; the exporter then takes the remaining stock of the toolpath's stock procedure.
            InitialStock: FindAny(Members.InitialStock) is not null,
            EntityExport: FindAny(Members.EntityExport) is not null,
            MultiStl: FindAny(Members.MultiStl) is not null,
            UcsTransform: ucsQuery || activeUcs,
            GeometryInUcs: FindAny(Members.EntityExport) is not null,
            ToolList: FindAny(Members.Tools) is not null,
            Procedures: Find(Members.ProcessManagerXml) is not null || Find(Members.ModelProcedures) is not null,
            UcsNames: ucsQuery || activeUcs || Find(Members.ProcessManagerXml) is not null);
    }

    public string? CimatronVersion { get; }

    public string AdapterVersion => Version;

    public CimatronCapabilities Capabilities { get; }

    /// <summary>Connects to the running Cimatron; throws <see cref="CimatronApiException"/> when it cannot.</summary>
    internal static CimatronComApi Connect(string? progId = null, string? cimatronFolder = null)
    {
        var name = string.IsNullOrWhiteSpace(progId) ? AppAccessProgId : progId.Trim();
        if (!OperatingSystem.IsWindows())
            throw new CimatronApiException("cimatron_not_available", "Cimatron automation needs Windows.");
        var type = Type.GetTypeFromProgID(name, throwOnError: false);
        var programFolder = CimatronTypeLibrary.FindProgramFolder(cimatronFolder);
        BinderProblems.Clear();
        Binder = CimatronBinder.FromProgramFolder(programFolder, BinderProblems);
        object access;
        try
        {
            if (type is not null)
            {
                access = Activator.CreateInstance(type)
                    ?? throw new CimatronApiException("cimatron_not_available", "Cimatron did not create the AppAccess object.");
            }
            else
            {
                // Not registered: AppAccess straight from the Cimatron Program folder (CimatronTypeLibrary).
                var folder = programFolder
                    ?? throw new CimatronApiException("cimatron_not_installed",
                        $"The Cimatron automation server '{name}' is not registered and no Cimatron Program folder with {CimatronTypeLibrary.AppAccessDll} was found (use --cimatron-folder).");
                access = CimatronTypeLibrary.CreateAppAccess(folder);
                ConnectedThrough = $"{System.IO.Path.Combine(folder, CimatronTypeLibrary.AppAccessDll)} (not registered; loaded directly)";
            }
        }
        catch (COMException exception)
        {
            throw new CimatronApiException("cimatron_not_available", "Cimatron is not running or refused the connection.", exception);
        }

        // GetActiveApplication first: at Meimad (2026-10-12) GetApplication reached a Cimatron instance with no open
        // document while the user's Cimatron had one. The first instance with an active document wins.
        ConnectTrace.Clear();
        object? application = null;
        object? document = null;
        CimatronApiException? accessFailure = null;
        // GetApplication starts a new, empty Cimatron when it cannot reach a running one (Meimad, 2026-10-12: two empty
        // instances left behind by probes). With Cimatron already running only GetActiveApplication is asked.
        var cimatronRunning = OperatingSystem.IsWindows() && CimatronRunningApplication.CimatronRunning();
        foreach (var member in Members.GetApplication.Where(member => HasMember(access, member)))
        {
            if (cimatronRunning && member == "GetApplication")
            {
                ConnectTrace.Add("AppAccess.GetApplication: skipped, it would start another Cimatron while Cimatron is already running");
                continue;
            }

            object? candidate;
            try
            {
                candidate = Call(access, member);
            }
            catch (CimatronApiException exception)
            {
                accessFailure ??= exception;
                ConnectTrace.Add($"AppAccess.{member}: {exception.Message}");
                continue;
            }

            if (candidate is null)
            {
                ConnectTrace.Add($"AppAccess.{member}: no object");
                continue;
            }

            var candidateDocument = ActiveDocument(candidate, $"AppAccess.{member}");
            application ??= candidate;
            if (candidateDocument is null) continue;
            application = candidate;
            document = candidateDocument;
            break;
        }

        if (application is null)
        {
            // AppAccess could not reach Cimatron by class (Cimatron's classes not registered): a running Cimatron's own entry.
            application = CimatronRunningApplication.Find(programFolder, ConnectTrace);
            if (application is null)
            {
                throw new CimatronApiException(accessFailure?.Code ?? "cimatron_not_available",
                    $"{accessFailure?.Message ?? "Cimatron returned no application object."} {CimatronRunningApplication.Advice()}", accessFailure);
            }

            ConnectedThrough = $"{ConnectedThrough ?? "the registered " + name}; application object from the running object table";
            document = ActiveDocument(application, "running object table");
        }

        if (document is null) TraceOpenDocuments(application);
        object? model = null;
        if (document is not null && HasMember(document, Members.Model))
        {
            model = CallOrGet(document, Members.Model);
            ConnectTrace.Add($"document.Model: {(model is null ? "no object" : "ok")}");
        }

        return new CimatronComApi(application, document, model);
    }

    // The application's active document, with what happened in the connection trace.
    private static object? ActiveDocument(object application, string source)
    {
        try
        {
            var document = Call(application, Members.GetActiveDoc);
            var title = document is null ? null : TryGetString(document, Members.Name);
            ConnectTrace.Add($"{source} -> GetActiveDoc: {(document is null ? "no active document" : $"'{title ?? "(no title)"}'")}");
            return document;
        }
        catch (CimatronApiException exception)
        {
            ConnectTrace.Add($"{source} -> GetActiveDoc: {exception.Message}");
            return null;
        }
    }

    // Read-only: how many documents this Cimatron instance has open (IPdm.GetOpenDocuments), for the probe.
    private static void TraceOpenDocuments(object application)
    {
        try
        {
            var pdm = HasMember(application, "GetPdm") ? Call(application, "GetPdm") : null;
            var open = pdm is not null && HasMember(pdm, "GetOpenDocuments") ? Call(pdm, "GetOpenDocuments") : null;
            var titles = Enumerate(open).Select(item => item as string ?? TryGetString(item, Members.Name) ?? item.ToString()).ToArray();
            ConnectTrace.Add($"open documents in this Cimatron instance: {(open is null ? "unknown" : titles.Length == 0 ? "none" : string.Join(", ", titles))}");
        }
        catch (CimatronApiException exception)
        {
            ConnectTrace.Add($"open documents: {exception.Message}");
        }
    }

    /// <summary>Lists which known members the application, the active document and the NC model expose (read-only probe).</summary>
    internal IEnumerable<(string Owner, string Member, bool Available)> Probe()
    {
        var names = new[]
        {
            Members.GetActiveDoc, Members.Model, Members.ProcessManagerXml, Members.PostByUcs, Members.PostToAll, Members.CuttersCsv,
            Members.JobsXml, Members.RemainingStock, Members.ModelProcedures, Members.ActiveUcs, Members.CreateFilter, Members.SetFilter,
            Members.SelectEntities
        }.Concat(Members.InitialStock).Concat(Members.EntityExport).Concat(Members.MultiStl).Concat(Members.Tools).Distinct();
        foreach (var name in names)
        {
            yield return ("application", name, HasMember(application, name));
            if (document is not null) yield return ("document", name, HasMember(document, name));
            if (model is not null) yield return ("model", name, HasMember(model, name));
        }
    }

    /// <summary>
    /// What the probe shows about the connected objects beyond member names: the interop interfaces each object answers
    /// to, the active UCS with the raw transformation values and the composed matrix, and the process-manager
    /// procedures as the adapter read them. Read-only towards Cimatron.
    /// </summary>
    internal IEnumerable<string> ProbeDetails()
    {
        if (Binder is not null)
        {
            yield return "Interfaces of the application object: " + Join(Binder.Interfaces(application));
            if (document is not null) yield return "Interfaces of the document object: " + Join(Binder.Interfaces(document));
            if (model is not null) yield return "Interfaces of the NC model object: " + Join(Binder.Interfaces(model));
        }

        if (model is null)
        {
            yield return "NC model: none (the active document has no Model, or no document is active)";
            yield break;
        }

        string activeLine;
        try
        {
            var ucs = HasMember(model, Members.ActiveUcs) ? CallOrGet(model, Members.ActiveUcs) : null;
            if (ucs is null)
            {
                activeLine = "Active UCS: none";
            }
            else
            {
                var name = TryGetString(ucs, Members.Name) ?? "(no name)";
                var (raw, vector, composed) = ReadTransformation(ucs);
                activeLine = $"Active UCS '{name}': Matrix [{Join(raw)}] Vector [{Join(vector)}] -> "
                    + (composed is null ? "no 4x4 matrix" : $"{composed.Value.Layout}: [{Join(composed.Value.Matrix)}]");
            }
        }
        catch (CimatronApiException exception)
        {
            activeLine = $"Active UCS: {exception.Message}";
        }

        yield return activeLine;
        yield return "UCS matrix layout (active UCS against Active2ModelTranslate): " + UcsLayout();

        string entityLine;
        try
        {
            var entities = UcsEntities().ToArray();
            var entityNames = entities.Select(ucs => TryGetString(ucs, Members.Name) ?? "(no name)").ToArray();
            entityLine = $"UCS entities (entity-type filter, cmUcs): {entities.Length}: {(entityNames.Length == 0 ? "none" : string.Join(", ", entityNames))}";
            if (entities.Length > 0 && Binder is not null)
                entityLine += Environment.NewLine + "Interfaces of the first UCS entity: " + Join(Binder.Interfaces(entities[0]));
        }
        catch (CimatronApiException exception)
        {
            entityLine = $"UCS entities: {exception.Message}";
        }

        yield return entityLine;

        var data = ProcessManager();
        yield return data is null
            ? "Process manager XML: not available"
            : $"Process manager XML: {data.Procedures.Count} procedure(s) read, UCS names {(data.UcsNames.Count == 0 ? "none" : string.Join(", ", data.UcsNames))}, "
                + $"stock-defining procedure(s) {(data.StockProcedures.Count == 0 ? "none" : string.Join(", ", data.StockProcedures))}";
        if (data is null || Find(Members.RemainingStock) is null) yield break;

        // Experiment: does the remaining stock of the stock-defining procedure give the initial stock? Cimatron 2026.0
        // has no initial-stock export; this writes only a temporary file.
        foreach (var id in data.StockProcedures.Take(2))
            yield return StockExperiment(id, $"stock-defining procedure {id}");
    }

    // The active UCS's composed matrix against IModel.Active2ModelTranslate of its origin and unit axes. Read-only.
    private string UcsLayout()
    {
        if (model is null || !HasMember(model, Members.ActiveUcs) || !HasMember(model, Members.ActiveToModel))
            return "not available (no Active2ModelTranslate)";
        try
        {
            var ucs = CallOrGet(model, Members.ActiveUcs);
            if (ucs is null) return "no active UCS";
            var (_, _, composed) = ReadTransformation(ucs);
            if (composed is null) return "the active UCS has no readable matrix";
            var points = new List<IReadOnlyList<double>>();
            foreach (var point in new[] { new double[] { 0, 0, 0 }, [1, 0, 0], [0, 1, 0], [0, 0, 1] })
            {
                var (result, outputs) = Invoke(model, Members.ActiveToModel, point);
                var translated = Doubles(outputs.Skip(1).FirstOrDefault(output => output is not null) ?? result);
                if (translated.Count < 3) return $"Active2ModelTranslate answered no point for ({Join(point)})";
                points.Add(translated);
            }

            return $"origin -> [{Join(points[0])}], axes -> [{Join(points[1])}] [{Join(points[2])}] [{Join(points[3])}]: "
                + UcsLayoutCheck.Compare(composed.Value.Matrix, points[0], points[1], points[2], points[3]);
        }
        catch (CimatronApiException exception)
        {
            return exception.Message;
        }
    }

    // ExportProceduresRemainingStock(id, cmNcModelUcs, temp file): what Cimatron answered, and the STL it wrote.
    private string StockExperiment(int procedureId, string label)
    {
        var file = Path.Combine(Path.GetTempPath(), $"meimad-stock-probe-{Guid.NewGuid():N}.stl");
        try
        {
            var (result, outputs) = Invoke(Require(Members.RemainingStock), Members.RemainingStock, procedureId, Members.ModelUcsType, file);
            var created = outputs.OfType<bool>().Cast<bool?>().FirstOrDefault() ?? result as bool?;
            if (!File.Exists(file)) return $"Remaining stock of {label}: answered {created?.ToString() ?? "nothing"}, no file written";
            var length = new FileInfo(file).Length;
            var bounds = StlBounds.Read(file);
            return $"Remaining stock of {label}: answered {created?.ToString() ?? "nothing"}, {length} bytes, "
                + (bounds is null
                    ? "not a binary STL (ASCII or other)"
                    : $"binary STL with {bounds.Value.Triangles} triangles, model box min [{Join(bounds.Value.Min)}] max [{Join(bounds.Value.Max)}]");
        }
        catch (CimatronApiException exception)
        {
            return $"Remaining stock of {label}: {exception.Message}";
        }
        finally
        {
            try
            {
                if (File.Exists(file)) File.Delete(file);
            }
            catch (IOException)
            {
            }
        }
    }

    public CimatronDocumentInfo GetActiveNcDocument()
    {
        if (document is null)
            throw new CimatronApiException("cimatron_no_nc_document", "No document is open in Cimatron.");
        if (Find(Members.PostByUcs) is null && Find(Members.PostToAll) is null)
            throw new CimatronApiException("cimatron_no_nc_document", "The active Cimatron document is not an NC document (no post-processing methods).");
        return new CimatronDocumentInfo(TryGetString(document, Members.Name) ?? "NC document", TryGetString(document, Members.Path), null);
    }

    public IReadOnlyList<CimatronProcedure> GetProcedures()
    {
        var data = ProcessManager();
        if (data is { Procedures.Count: > 0 }) return data.Machining;
        if (model is null || !HasMember(model, Members.ModelProcedures)) return [];
        var result = new List<CimatronProcedure>();
        foreach (var item in Enumerate(CallOrGet(model, Members.ModelProcedures)))
        {
            var id = TryGetInt(item, Members.EntityId);
            if (id is null) continue;
            result.Add(new CimatronProcedure(id.Value, TryGetString(item, Members.Name) ?? $"Procedure {id}", null, true));
        }

        return result;
    }

    public IReadOnlyList<string> GetUcsNames()
    {
        var names = new List<string>();
        try
        {
            names.AddRange(UcsEntities().Select(ucs => TryGetString(ucs, Members.Name)).OfType<string>());
        }
        catch (CimatronApiException)
        {
            // The entity query is one of three sources; the others still answer.
        }

        if (model is not null && HasMember(model, Members.ActiveUcs))
        {
            var active = CallOrGet(model, Members.ActiveUcs);
            if (active is not null && TryGetString(active, Members.Name) is { } name) names.Add(name);
        }

        if (ProcessManager() is { } data) names.AddRange(data.UcsNames);
        return names.Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public CimatronUcs? GetUcs(string name)
    {
        var ucs = FindUcs(name);
        if (ucs is null) return null;
        var (_, _, composed) = ReadTransformation(ucs);
        return composed is null ? null : new CimatronUcs(name, composed.Value.Matrix);
    }

    /// <summary>Cimatron 2026.0 lists cutters only through <c>ExportCuttersAndHoldersToCSV</c>; the API has no tool list.</summary>
    public IReadOnlyList<CimatronTool> GetTools()
    {
        var owner = FindAny(Members.Tools);
        if (owner is null) return [];
        var result = new List<CimatronTool>();
        foreach (var item in Enumerate(CallOrGet(owner.Value.Target, owner.Value.Name)))
        {
            var number = TryGetInt(item, ["Number", "ToolNumber", "TNumber"]);
            if (number is null) continue;
            result.Add(new CimatronTool(number.Value, TryGetString(item, Members.Name) ?? $"T{number}", TryGetString(item, ["Type"]),
                TryGetDouble(item, ["Diameter"]), TryGetDouble(item, ["CornerRadius", "Radius"]), TryGetDouble(item, ["FluteLength", "CuttingLength"]),
                TryGetDouble(item, ["GaugeLength", "Length"]), TryGetString(item, ["HolderName"]),
                TryGetDouble(item, ["HolderDiameter"]), TryGetDouble(item, ["HolderLength"])));
        }

        return result;
    }

    public IReadOnlyList<string> PostByUcs(string postName, string outputFolder, bool startHome, string ucsName)
    {
        var before = Snapshot(outputFolder);
        var (_, outputs) = Invoke(Require(Members.PostByUcs), Members.PostByUcs, postName, outputFolder, startHome, ucsName);
        return Created(outputFolder, before, outputs);
    }

    /// <summary>
    /// <c>PostByUcsByIdVec(post, folder, nodes, ucs, outputFileName, out result)</c>: posts only the given tree nodes
    /// (toolpath or procedure ids from the process manager) into <paramref name="outputName"/> in the folder.
    /// </summary>
    public IReadOnlyList<string> PostSelected(string postName, string outputFolder, IReadOnlyList<int> nodeIds, string ucsName, string outputName)
    {
        var before = Snapshot(outputFolder);
        var (result, outputs) = Invoke(Require(Members.PostSelected), Members.PostSelected, postName, outputFolder, nodeIds.ToArray(), ucsName, outputName);
        var created = Created(outputFolder, before, outputs);
        if (created.Length == 0 && (outputs.OfType<bool>().Cast<bool?>().LastOrDefault() ?? result as bool?) == false)
            throw new CimatronApiException("cimatron_call_failed", $"PostByUcsByIdVec answered false for nodes {string.Join(", ", nodeIds)}.");
        return created;
    }

    public IReadOnlyList<CimatronToolpath> GetToolpaths() => ProcessManager()?.Toolpaths ?? [];

    public IReadOnlyList<string> PostToAll(string postName, string outputFolder, bool startHome)
    {
        var before = Snapshot(outputFolder);
        var (_, outputs) = Invoke(Require(Members.PostToAll), Members.PostToAll, postName, outputFolder, startHome);
        return Created(outputFolder, before, outputs);
    }

    public void ExportCuttersAndHolders(string cuttersCsv, string holdersCsv, string materialsCsv) =>
        Call(Require(Members.CuttersCsv), Members.CuttersCsv, Array.Empty<object>(), cuttersCsv, holdersCsv, materialsCsv);

    /// <summary>
    /// <c>NcExportJobsData(iOutputPath, out oNumberOfJobs)</c> takes an output path; whether Cimatron writes one file at that
    /// path or files into that folder is settled by <see cref="JobsExportFiles.Normalize"/> afterwards.
    /// </summary>
    public void ExportJobsData(string xmlPath)
    {
        var exportFolder = Path.Combine(Path.GetDirectoryName(xmlPath)!, "jobs-export");
        Directory.CreateDirectory(exportFolder);
        var target = Require(Members.JobsXml);
        Invoke(target, Members.JobsXml, exportFolder);
        if (Directory.Exists(exportFolder) && !Directory.EnumerateFileSystemEntries(exportFolder).Any() && !File.Exists(xmlPath))
        {
            // Nothing in the folder: Cimatron may want a file path.
            Directory.Delete(exportFolder);
            Invoke(target, Members.JobsXml, xmlPath);
        }

        JobsExportFiles.Normalize(exportFolder, xmlPath);
    }

    public void ExportRemainingStock(int procedureId, CimatronUcsFrame frame, string stlPath)
    {
        var ucsType = frame == CimatronUcsFrame.Procedure ? Members.ProcedureUcsType : Members.ModelUcsType;
        var (result, outputs) = Invoke(Require(Members.RemainingStock), Members.RemainingStock, procedureId, ucsType, stlPath);
        var created = outputs.OfType<bool>().Cast<bool?>().FirstOrDefault() ?? result as bool?;
        if (created == false && !File.Exists(stlPath))
            throw new CimatronApiException("cimatron_call_failed", $"Cimatron wrote no remaining stock for procedure {procedureId} (ExportProceduresRemainingStock answered false).");
    }

    public void ExportInitialStock(CimatronUcsFrame frame, string stlPath)
    {
        var owner = FindAny(Members.InitialStock)
            ?? throw new CimatronApiException("capability_unavailable", "This Cimatron exposes no initial-stock export.");
        Call(owner.Target, owner.Name, frame == CimatronUcsFrame.Procedure ? Members.ProcedureUcsType : Members.ModelUcsType, stlPath);
    }

    public IReadOnlyList<CimatronGeometryFile> ExportPartGeometry(string ucsName, string folder)
    {
        var owner = FindAny(Members.EntityExport);
        var method = "entity";
        if (owner is null)
        {
            owner = FindAny(Members.MultiStl);
            method = "multi-stl";
        }

        if (owner is null) throw new CimatronApiException("capability_unavailable", "This Cimatron exposes no target/fixture geometry export.");
        var before = Snapshot(folder);
        Call(owner.Value.Target, owner.Value.Name, ucsName, folder);
        return Created(folder, before, [])
            .Select(path => new CimatronGeometryFile(path, GeometryClassifier.Classify(path), Path.GetFileNameWithoutExtension(path), method))
            .ToArray();
    }

    /// <summary>
    /// Writes Cimatron's process manager (procedures, UCS, tools) as XML to <paramref name="path"/>; read-only towards
    /// Cimatron. The probe saves one so the procedure accessors can be matched to the real format.
    /// </summary>
    internal void ExportProcessManagerXml(string path) =>
        Call(Require(Members.ProcessManagerXml), Members.ProcessManagerXml, true, path);

    public void Dispose()
    {
        if (model is not null && Marshal.IsComObject(model)) Marshal.FinalReleaseComObject(model);
        if (document is not null && Marshal.IsComObject(document)) Marshal.FinalReleaseComObject(document);
        if (Marshal.IsComObject(application)) Marshal.FinalReleaseComObject(application);
    }

    // The process manager XML, read once through a temporary file (GetProcessManagerAsXML writes a file).
    private ProcessManagerData? ProcessManager()
    {
        if (processManager is not null) return processManager;
        var owner = Find(Members.ProcessManagerXml);
        if (owner is null) return null;
        var file = Path.Combine(Path.GetTempPath(), $"meimad-process-manager-{Guid.NewGuid():N}.xml");
        try
        {
            Call(owner, Members.ProcessManagerXml, false, file);
            if (!File.Exists(file)) return null;
            processManager = ProcessManagerXml.Parse(File.ReadAllText(file));
            return processManager;
        }
        catch (CimatronApiException)
        {
            return null;
        }
        finally
        {
            try
            {
                if (File.Exists(file)) File.Delete(file);
            }
            catch (IOException)
            {
            }
        }
    }

    // UCS entities of the NC model through its entity query: CreateFilter(cmFilterEntityType) -> IFilterType.Add(cmUcs)
    // -> SetFilter -> Select. (cimatron-com-3 passed cmUcs to CreateFilter, which is cmFilterWireBody there: the probe
    // of 2026-10-12 got about 100 unnamed wire bodies.)
    private IEnumerable<object> UcsEntities()
    {
        if (model is null || !HasMember(model, Members.CreateFilter) || !HasMember(model, Members.SetFilter) || !HasMember(model, Members.SelectEntities))
            return [];
        var filter = Result(Invoke(model, Members.CreateFilter, Members.EntityTypeFilter));
        if (filter is null || !HasMember(filter, Members.FilterAdd)) return [];
        Call(filter, Members.FilterAdd, Members.UcsEntityType);
        Call(model, Members.SetFilter, filter);
        var list = Result(Invoke(model, Members.SelectEntities));
        return Enumerate(list).ToArray();
    }

    private object? FindUcs(string name)
    {
        try
        {
            foreach (var ucs in UcsEntities())
            {
                if (string.Equals(TryGetString(ucs, Members.Name), name, StringComparison.OrdinalIgnoreCase)) return ucs;
            }
        }
        catch (CimatronApiException)
        {
            // Fall through to the active UCS.
        }

        if (model is null || !HasMember(model, Members.ActiveUcs)) return null;
        var active = CallOrGet(model, Members.ActiveUcs);
        return active is not null && string.Equals(TryGetString(active, Members.Name), name, StringComparison.OrdinalIgnoreCase) ? active : null;
    }

    // IUcs.Transformation -> IModelTransformation.Matrix / Vector, composed to a row-major 4x4 matrix.
    private static (IReadOnlyList<double> Raw, IReadOnlyList<double> Vector, (IReadOnlyList<double> Matrix, string Layout)? Composed) ReadTransformation(object ucs)
    {
        var transformation = HasMember(ucs, Members.Transformation) ? CallOrGet(ucs, Members.Transformation) : null;
        if (transformation is null) return ([], [], null);
        IReadOnlyList<double> raw = [];
        foreach (var member in Members.Matrix)
        {
            if (!HasMember(transformation, member)) continue;
            raw = Doubles(CallOrGet(transformation, member));
            if (raw.Count > 0) break;
        }

        var vector = HasMember(transformation, Members.Vector) ? Doubles(CallOrGet(transformation, Members.Vector)) : [];
        return (raw, vector, UcsMatrix.Compose(raw, vector));
    }

    private static IReadOnlyList<double> Doubles(object? values)
    {
        var result = new List<double>();
        foreach (var item in Enumerate(values))
        {
            if (item is IConvertible convertible)
            {
                try
                {
                    result.Add(convertible.ToDouble(CultureInfo.InvariantCulture));
                }
                catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
                {
                    return [];
                }
            }
        }

        return result;
    }

    private object Require(string member) =>
        Find(member) ?? throw new CimatronApiException("capability_unavailable", $"This Cimatron does not expose {member}.");

    private object? Find(string member)
    {
        if (model is not null && HasMember(model, member)) return model;
        if (document is not null && HasMember(document, member)) return document;
        return HasMember(application, member) ? application : null;
    }

    private (object Target, string Name)? FindAny(IEnumerable<string> members)
    {
        foreach (var member in members)
        {
            if (Find(member) is { } target) return (target, member);
        }

        return null;
    }

    private static HashSet<string> Snapshot(string folder) =>
        Directory.Exists(folder) ? new(Directory.GetFiles(folder), StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);

    // New files in the folder, plus the file Cimatron named in an out parameter (oOutputFileName) when it exists.
    private static string[] Created(string folder, HashSet<string> before, object?[] outputs)
    {
        var created = Directory.Exists(folder)
            ? Directory.GetFiles(folder).Where(path => !before.Contains(path)).ToList()
            : [];
        foreach (var named in outputs.OfType<string>().Where(text => !string.IsNullOrWhiteSpace(text)))
        {
            var path = Path.IsPathRooted(named) ? named : Path.Combine(folder, named);
            if (File.Exists(path) && !created.Contains(path, StringComparer.OrdinalIgnoreCase)) created.Add(path);
        }

        return created.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string Join(IEnumerable<double> values) => string.Join(", ", values.Select(value => value.ToString("G6", CultureInfo.InvariantCulture)));

    private static string Join(IEnumerable<string> values)
    {
        var list = values.ToArray();
        return list.Length == 0 ? "(none of the known interop interfaces)" : string.Join(", ", list);
    }

    private static object? Result((object? Result, object?[] Outputs) call) =>
        call.Result ?? call.Outputs.LastOrDefault(value => value is not null);

    private static object? Call(object target, string member, params object[] arguments) => Invoke(target, member, arguments).Result;

    // Calls a member and returns its result with the values of its out parameters (binder) or none (IDispatch).
    private static (object? Result, object?[] Outputs) Invoke(object target, string member, params object[] arguments)
    {
        try
        {
            if (Binder is not null && Binder.TryInvoke(target, member, arguments, out var result, out var outputs)) return (result, outputs);
            return (target.GetType().InvokeMember(member, BindingFlags.InvokeMethod | BindingFlags.GetProperty, null, target, arguments, CultureInfo.InvariantCulture), []);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw Failed(member, exception.InnerException);
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or ArgumentException)
        {
            throw Failed(member, exception);
        }
        catch (MissingMethodException exception)
        {
            throw new CimatronApiException("capability_unavailable", $"Cimatron does not expose {member}.", exception);
        }
    }

    // TYPE_E_LIBNOTREGISTERED, REGDB_E_IIDNOTREG and E_NOINTERFACE: Windows cannot pass the call to Cimatron without
    // Cimatron's type libraries (and with them its interfaces) registered.
    private static CimatronApiException Failed(string member, Exception exception)
    {
        var registration = exception is InvalidCastException
            || exception.HResult is unchecked((int)0x8002801D) or unchecked((int)0x80040155) or unchecked((int)0x80004002);
        var elevated = OperatingSystem.IsWindows() && CimatronRunningApplication.Elevated;
        var hint = !registration ? ""
            : elevated
                ? " The exporter runs as administrator: Windows ignores a per-user registration of Cimatron's type libraries for administrator processes. Run --register-cimatron once from this administrator window (it then registers them for every user), or run the exporter from a normal window (Win+R, cmd, Enter)."
                : " Cimatron's type libraries are not registered on this PC: run the exporter once with --register-cimatron (current user, no administrator), or repair the Cimatron installation.";
        return new CimatronApiException("cimatron_call_failed", $"Cimatron {member} failed: {exception.Message}{hint}", exception);
    }

    private static object? CallOrGet(object target, string member)
    {
        try
        {
            if (Binder is not null && Binder.TryInvoke(target, member, [], out var result, out var outputs)) return result ?? outputs.LastOrDefault(value => value is not null);
            return target.GetType().InvokeMember(member, BindingFlags.GetProperty | BindingFlags.InvokeMethod, null, target, null, CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is COMException or TargetInvocationException or MissingMethodException or InvalidCastException or ArgumentException)
        {
            return null;
        }
    }

    // A .NET collection, a SAFEARRAY (any rank), or a COM list with Count and Item (0- or 1-based).
    private static IEnumerable<object> Enumerate(object? collection)
    {
        switch (collection)
        {
            case null:
                yield break;
            case string:
                yield return collection;
                yield break;
            case System.Collections.IEnumerable enumerable:
                foreach (var item in enumerable) if (item is not null) yield return item;
                yield break;
        }

        var count = TryGetInt(collection, ["Count"]);
        if (count is null or <= 0) yield break;
        var first = Item(collection, 0);
        var offset = first is null ? 1 : 0;
        for (var index = 0; index < count; index++)
        {
            var item = index == 0 && offset == 0 ? first : Item(collection, index + offset);
            if (item is not null) yield return item;
        }
    }

    private static object? Item(object collection, int index)
    {
        try
        {
            if (Binder is not null && Binder.TryInvoke(collection, "Item", [index], out var result, out var outputs)) return result ?? outputs.LastOrDefault(value => value is not null);
            return collection.GetType().InvokeMember("Item", BindingFlags.InvokeMethod | BindingFlags.GetProperty, null, collection, [index], CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is COMException or TargetInvocationException or MissingMethodException or ArgumentException or InvalidCastException)
        {
            return null;
        }
    }

    private static string? TryGetString(object target, IEnumerable<string> members) =>
        members.Where(member => HasMember(target, member)).Select(member => CallOrGet(target, member)?.ToString()).FirstOrDefault(value => value is not null);

    private static int? TryGetInt(object target, IEnumerable<string> members)
    {
        foreach (var member in members.Where(member => HasMember(target, member)))
        {
            var value = CallOrGet(target, member);
            if (value is not null && int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                return number;
        }

        return null;
    }

    private static double? TryGetDouble(object target, IEnumerable<string> members)
    {
        foreach (var member in members.Where(member => HasMember(target, member)))
        {
            var value = CallOrGet(target, member);
            if (value is not null && double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                return number;
        }

        return null;
    }

    /// <summary>Asks the interop interfaces, then IDispatch, whether the object has a member, without calling it.</summary>
    internal static bool HasMember(object target, string member)
    {
        if (Binder is not null && Binder.Has(target, member)) return true;
        if (!OperatingSystem.IsWindows() || !Marshal.IsComObject(target))
            return target.GetType().GetMember(member, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase).Length > 0;
        if (target is not IDispatchNames dispatch) return false;
        var iid = Guid.Empty;
        var ids = new int[1];
        return dispatch.GetIDsOfNames(ref iid, [member], 1, 0x0409, ids) == 0;
    }

    [ComImport]
    [Guid("00020400-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDispatchNames
    {
        [PreserveSig]
        int GetTypeInfoCount(out uint count);

        [PreserveSig]
        int GetTypeInfo(uint index, int lcid, out IntPtr typeInfo);

        [PreserveSig]
        int GetIDsOfNames(
            ref Guid riid,
            [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] names,
            uint count,
            int lcid,
            [MarshalAs(UnmanagedType.LPArray), Out] int[] dispatchIds);
    }
}

/// <summary>Classifies Cimatron Multi STL / Data Interface files by name (TARGET / FIXTURE / OTHER).</summary>
internal static class GeometryClassifier
{
    internal static string Classify(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path).ToUpperInvariant();
        if (name.Contains("FIXTURE", StringComparison.Ordinal) || name.Contains("CLAMP", StringComparison.Ordinal)
            || name.Contains("VISE", StringComparison.Ordinal) || name.Contains("JIG", StringComparison.Ordinal))
            return GeometryRoles.Fixture;
        if (name.Contains("TARGET", StringComparison.Ordinal) || name.Contains("PART", StringComparison.Ordinal))
            return GeometryRoles.Target;
        return GeometryRoles.Other;
    }
}
