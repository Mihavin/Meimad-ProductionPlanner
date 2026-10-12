using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Meimad.CimatronExporter;

/// <summary>
/// <see cref="ICimatronApi"/> over a running Cimatron through late-bound COM (IDispatch), so no
/// Cimatron interop assembly is needed to build or ship the exporter. Adapter version
/// <see cref="Version"/>: it connects through <c>CimAppAccess.AppAccess.GetApplication()</c>
/// (exporter definition §3.1), takes the active document, and finds each API method by name on the
/// active document first and the application second. Capability detection asks IDispatch for the
/// member names before anything is called, so a Cimatron version without a method reports it as
/// unavailable instead of failing half-way. The method names in <see cref="Members"/> follow the
/// exporter definition; the procedure, UCS, tool and geometry accessors are best guesses.
/// UNVERIFIED: this adapter has never run against a real Cimatron — run <c>--probe</c> first.
/// </summary>
internal sealed class CimatronComApi : ICimatronApi
{
    internal const string Version = "cimatron-com-1";
    internal const string AppAccessProgId = "CimAppAccess.AppAccess";

    /// <summary>The late-bound member names, in one place so a field test can adjust them.</summary>
    internal static class Members
    {
        internal const string GetApplication = "GetApplication";
        internal const string GetActiveDoc = "GetActiveDoc";
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
        internal static readonly string[] Name = ["Name", "GetName"];
        internal static readonly string[] Path = ["Path", "FullName", "GetPath"];
    }

    private readonly object application;
    private readonly object? document;

    private CimatronComApi(object application, object? document)
    {
        this.application = application;
        this.document = document;
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
    internal static CimatronComApi Connect()
    {
        if (!OperatingSystem.IsWindows())
            throw new CimatronApiException("cimatron_not_available", "Cimatron automation needs Windows.");
        var type = Type.GetTypeFromProgID(AppAccessProgId, throwOnError: false)
            ?? throw new CimatronApiException("cimatron_not_installed",
                $"The Cimatron automation server '{AppAccessProgId}' is not registered on this PC.");
        object access;
        try
        {
            access = Activator.CreateInstance(type)
                ?? throw new CimatronApiException("cimatron_not_available", "Cimatron did not create the AppAccess object.");
        }
        catch (COMException exception)
        {
            throw new CimatronApiException("cimatron_not_available", "Cimatron is not running or refused the connection.", exception);
        }

        var application = Call(access, Members.GetApplication)
            ?? throw new CimatronApiException("cimatron_not_available", "Cimatron returned no application object.");
        object? document = null;
        try
        {
            document = Call(application, Members.GetActiveDoc);
        }
        catch (CimatronApiException)
        {
            // Reported by GetActiveNcDocument.
        }

        return new CimatronComApi(application, document);
    }

    /// <summary>Lists which known members the application and the active document expose (read-only probe).</summary>
    internal IEnumerable<(string Owner, string Member, bool Available)> Probe()
    {
        var names = new[]
        {
            Members.GetActiveDoc, Members.PostByUcs, Members.PostToAll, Members.CuttersCsv, Members.JobsXml, Members.RemainingStock
        }.Concat(Members.InitialStock).Concat(Members.Procedures).Concat(Members.UcsList).Concat(Members.Ucs)
         .Concat(Members.Tools).Concat(Members.EntityExport).Concat(Members.MultiStl).Distinct();
        foreach (var name in names)
        {
            yield return ("application", name, HasMember(application, name));
            if (document is not null) yield return ("document", name, HasMember(document, name));
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

    public void Dispose()
    {
        if (document is not null && Marshal.IsComObject(document)) Marshal.FinalReleaseComObject(document);
        if (Marshal.IsComObject(application)) Marshal.FinalReleaseComObject(application);
    }

    private object Require(string member) =>
        Find(member) ?? throw new CimatronApiException("capability_unavailable", $"This Cimatron does not expose {member}.");

    private object? Find(string member)
    {
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
            return target.GetType().InvokeMember(member, BindingFlags.InvokeMethod, null, target, arguments, CultureInfo.InvariantCulture);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is COMException com)
        {
            throw new CimatronApiException("cimatron_call_failed", $"Cimatron {member} failed: {com.Message}", com);
        }
        catch (COMException exception)
        {
            throw new CimatronApiException("cimatron_call_failed", $"Cimatron {member} failed: {exception.Message}", exception);
        }
        catch (MissingMethodException exception)
        {
            throw new CimatronApiException("capability_unavailable", $"Cimatron does not expose {member}.", exception);
        }
    }

    private static object? CallOrGet(object target, string member)
    {
        try
        {
            return target.GetType().InvokeMember(member, BindingFlags.GetProperty | BindingFlags.InvokeMethod, null, target, null, CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is COMException or TargetInvocationException or MissingMethodException)
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

    /// <summary>Asks IDispatch whether the object has a member, without calling it.</summary>
    internal static bool HasMember(object target, string member)
    {
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
