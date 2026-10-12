using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Meimad.CimatronExporter;

/// <summary>
/// <see cref="ICimatronApi"/> over a running Cimatron through late-bound COM, so no Cimatron interop
/// assembly is needed to build or ship the exporter. Adapter version <see cref="Version"/>: it connects
/// through <c>CimAppAccess.AppAccess.GetApplication()</c> (exporter definition §3.1), takes the active
/// document and its NC model, and finds each API method by name on the model, the document and the
/// application in that order. A member is called through the vtable interfaces of Cimatron's installed
/// interop assemblies when they have it (<see cref="CimatronBinder"/>), else through IDispatch.
/// Capability detection looks the member names up before anything is called, so a Cimatron version
/// without a method reports it as unavailable instead of failing half-way. The names in
/// <see cref="Members"/> are those of the Cimatron 2026.0 type libraries (probe of 2026-10-12); the
/// procedure, UCS, tool and geometry accessors are still best guesses.
/// UNVERIFIED: this adapter has never completed a call against a real Cimatron — run <c>--probe</c> first.
/// </summary>
internal sealed class CimatronComApi : ICimatronApi
{
    internal const string Version = "cimatron-com-2";
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
        internal static readonly string[] GetApplication = ["GetApplication", "GetActiveApplication"];
        internal const string GetActiveDoc = "GetActiveDoc";
        internal const string Model = "Model";
        internal const string ProcessManagerXml = "GetProcessManagerAsXML";
        internal const string PostByUcs = "PostByUcsToAllNcProcedures";
        internal const string PostToAll = "ExecutePostToAllNcProcedures";
        internal const string CuttersCsv = "ExportCuttersAndHoldersToCSV";
        internal const string JobsXml = "NcExportJobsData";
        internal const string RemainingStock = "ExportProceduresRemainingStock";
        internal static readonly string[] InitialStock = ["ExportStockToStl", "ExportStock"];
        internal static readonly string[] Procedures = ["GetProcedures", "Procedures"];
        internal static readonly string[] UcsList = ["GetUcsList", "GetUCSList", "UcsList"];
        internal static readonly string[] Ucs = ["GetUcs", "GetUCS"];
        internal static readonly string[] UcsMatrix = ["GetMatrix", "Matrix", "GetTransformation"];
        internal static readonly string[] Tools = ["GetCutters", "GetTools"];
        internal static readonly string[] EntityExport = ["ExportProcedureGeometry", "ExportPartsToStl"];
        internal static readonly string[] MultiStl = ["ExportMultiStl", "ExportSimulatorMultiStl"];
        internal static readonly string[] Version = ["Version", "GetVersion"];
        internal static readonly string[] Name = ["Title", "Name", "GetName"];
        internal static readonly string[] Path = ["GetPath", "Path", "FullName"];
    }

    private readonly object application;
    private readonly object? document;
    private readonly object? model;

    private CimatronComApi(object application, object? document, object? model)
    {
        this.application = application;
        this.document = document;
        this.model = model;
        CimatronVersion = TryGetString(application, Members.Version);
        Capabilities = new CimatronCapabilities(
            PostByUcs: Find(Members.PostByUcs) is not null,
            PostToAll: Find(Members.PostToAll) is not null,
            CuttersAndHoldersCsv: Find(Members.CuttersCsv) is not null,
            JobsXml: Find(Members.JobsXml) is not null,
            RemainingStock: Find(Members.RemainingStock) is not null,
            InitialStock: FindAny(Members.InitialStock) is not null,
            EntityExport: FindAny(Members.EntityExport) is not null,
            MultiStl: FindAny(Members.MultiStl) is not null,
            UcsTransform: FindAny(Members.Ucs) is not null,
            GeometryInUcs: FindAny(Members.EntityExport) is not null,
            ToolList: FindAny(Members.Tools) is not null);
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

        var getApplication = Members.GetApplication.FirstOrDefault(member => HasMember(access, member)) ?? Members.GetApplication[0];
        ConnectTrace.Clear();
        object? application = null;
        CimatronApiException? accessFailure = null;
        try
        {
            application = Call(access, getApplication);
            ConnectTrace.Add($"AppAccess.{getApplication}: {(application is null ? "no object" : "ok")}");
        }
        catch (CimatronApiException exception)
        {
            accessFailure = exception;
            ConnectTrace.Add($"AppAccess.{getApplication}: {exception.Message}");
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
        }
        object? document = null;
        object? model = null;
        try
        {
            document = Call(application, Members.GetActiveDoc);
            if (document is not null && HasMember(document, Members.Model)) model = CallOrGet(document, Members.Model);
        }
        catch (CimatronApiException)
        {
            // Reported by GetActiveNcDocument.
        }

        return new CimatronComApi(application, document, model);
    }

    /// <summary>Lists which known members the application and the active document expose (read-only probe).</summary>
    internal IEnumerable<(string Owner, string Member, bool Available)> Probe()
    {
        var names = new[]
        {
            Members.GetActiveDoc, Members.Model, Members.ProcessManagerXml, Members.PostByUcs, Members.PostToAll, Members.CuttersCsv, Members.JobsXml, Members.RemainingStock
        }.Concat(Members.InitialStock).Concat(Members.Procedures).Concat(Members.UcsList).Concat(Members.Ucs)
         .Concat(Members.Tools).Concat(Members.EntityExport).Concat(Members.MultiStl).Distinct();
        foreach (var name in names)
        {
            yield return ("application", name, HasMember(application, name));
            if (document is not null) yield return ("document", name, HasMember(document, name));
            if (model is not null) yield return ("model", name, HasMember(model, name));
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
        var owner = FindAny(Members.Procedures);
        if (owner is null) return [];
        var collection = CallOrGet(owner.Value.Target, owner.Value.Name);
        var result = new List<CimatronProcedure>();
        foreach (var item in Enumerate(collection))
        {
            var id = TryGetInt(item, ["Id", "ID", "GetId"]);
            if (id is null) continue;
            result.Add(new CimatronProcedure(
                id.Value,
                TryGetString(item, Members.Name) ?? $"Procedure {id}",
                TryGetString(item, ["UcsName", "UCSName", "GetUcsName"]),
                TryGetBool(item, ["HasToolpath", "IsCalculated", "Calculated"]) ?? true,
                TryGetString(item, ["OperationName", "Comment"])));
        }

        return result;
    }

    public IReadOnlyList<string> GetUcsNames()
    {
        var owner = FindAny(Members.UcsList);
        if (owner is null) return [];
        return Enumerate(CallOrGet(owner.Value.Target, owner.Value.Name))
            .Select(item => item as string ?? TryGetString(item, Members.Name))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .ToArray();
    }

    public CimatronUcs? GetUcs(string name)
    {
        var owner = FindAny(Members.Ucs);
        if (owner is null) return null;
        var ucs = Call(owner.Value.Target, owner.Value.Name, name);
        if (ucs is null) return null;
        foreach (var member in Members.UcsMatrix)
        {
            if (!HasMember(ucs, member)) continue;
            var values = Enumerate(CallOrGet(ucs, member)).Select(item => Convert.ToDouble(item, CultureInfo.InvariantCulture)).ToArray();
            if (values.Length == 16) return new CimatronUcs(name, values);
            if (values.Length == 12) return new CimatronUcs(name, [.. values, 0, 0, 0, 1]);
        }

        return null;
    }

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
        Call(Require(Members.PostByUcs), Members.PostByUcs, postName, outputFolder, startHome, ucsName);
        return Created(outputFolder, before);
    }

    public IReadOnlyList<string> PostToAll(string postName, string outputFolder, bool startHome)
    {
        var before = Snapshot(outputFolder);
        Call(Require(Members.PostToAll), Members.PostToAll, postName, outputFolder, startHome);
        return Created(outputFolder, before);
    }

    public void ExportCuttersAndHolders(string cuttersCsv, string holdersCsv, string materialsCsv) =>
        Call(Require(Members.CuttersCsv), Members.CuttersCsv, Array.Empty<object>(), cuttersCsv, holdersCsv, materialsCsv);

    public void ExportJobsData(string xmlPath) => Call(Require(Members.JobsXml), Members.JobsXml, xmlPath);

    public void ExportRemainingStock(int procedureId, string ucsName, string stlPath) =>
        Call(Require(Members.RemainingStock), Members.RemainingStock, procedureId, ucsName, stlPath);

    public void ExportInitialStock(string ucsName, string stlPath)
    {
        var owner = FindAny(Members.InitialStock)
            ?? throw new CimatronApiException("capability_unavailable", "This Cimatron exposes no initial-stock export.");
        Call(owner.Target, owner.Name, ucsName, stlPath);
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
        return Created(folder, before)
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

    private static string[] Created(string folder, HashSet<string> before) =>
        Directory.Exists(folder) ? Directory.GetFiles(folder).Where(path => !before.Contains(path)).Order().ToArray() : [];

    private static object? Call(object target, string member, params object[] arguments)
    {
        try
        {
            if (Binder is not null && Binder.TryInvoke(target, member, arguments, out var result, out _)) return result;
            return target.GetType().InvokeMember(member, BindingFlags.InvokeMethod, null, target, arguments, CultureInfo.InvariantCulture);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is COMException or InvalidCastException)
        {
            throw Failed(member, exception.InnerException!);
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException)
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
        var hint = registration
            ? " Cimatron's type libraries are not registered on this PC: run the exporter once with --register-cimatron (current user, no administrator), or repair the Cimatron installation."
            : "";
        return new CimatronApiException("cimatron_call_failed", $"Cimatron {member} failed: {exception.Message}{hint}", exception);
    }

    private static object? CallOrGet(object target, string member)
    {
        try
        {
            if (Binder is not null && Binder.TryInvoke(target, member, [], out var result, out _)) return result;
            return target.GetType().InvokeMember(member, BindingFlags.GetProperty | BindingFlags.InvokeMethod, null, target, null, CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is COMException or TargetInvocationException or MissingMethodException or InvalidCastException)
        {
            return null;
        }
    }

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
        for (var index = 0; index < (count ?? 0); index++)
        {
            object? item = null;
            try
            {
                item = collection.GetType().InvokeMember("Item", BindingFlags.InvokeMethod | BindingFlags.GetProperty, null, collection, [index], CultureInfo.InvariantCulture);
            }
            catch (Exception exception) when (exception is COMException or TargetInvocationException or MissingMethodException)
            {
            }

            if (item is not null) yield return item;
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

    private static bool? TryGetBool(object target, IEnumerable<string> members)
    {
        foreach (var member in members.Where(member => HasMember(target, member)))
        {
            if (CallOrGet(target, member) is bool value) return value;
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
