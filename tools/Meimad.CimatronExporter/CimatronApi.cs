namespace Meimad.CimatronExporter;

/// <summary>What the connected Cimatron (or a fake) can do; detected once at connection.</summary>
internal sealed record CimatronCapabilities(
    bool PostByUcs,
    bool PostToAll,
    bool CuttersAndHoldersCsv,
    bool JobsXml,
    bool RemainingStock,
    bool InitialStock,
    bool EntityExport,
    bool MultiStl,
    bool UcsTransform,
    bool GeometryInUcs,
    bool ToolList,
    bool Procedures = true,
    bool UcsNames = true,
    bool PostSelected = false)
{
    public IEnumerable<(string Name, bool Available)> All()
    {
        yield return ("Procedure list", Procedures);
        yield return ("UCS list", UcsNames);
        yield return ("PostByUcsToAllNcProcedures", PostByUcs);
        yield return ("ExecutePostToAllNcProcedures", PostToAll);
        yield return ("PostByUcsByIdVec (post one toolpath)", PostSelected);
        yield return ("ExportCuttersAndHoldersToCSV", CuttersAndHoldersCsv);
        yield return ("NcExportJobsData", JobsXml);
        yield return ("ExportProceduresRemainingStock", RemainingStock);
        yield return ("Initial stock export", InitialStock);
        yield return ("Entity export (target/fixture)", EntityExport);
        yield return ("Multi STL export", MultiStl);
        yield return ("UCS transformation", UcsTransform);
        yield return ("Geometry export in procedure UCS", GeometryInUcs);
        yield return ("Tool list", ToolList);
    }
}

internal sealed record CimatronDocumentInfo(string Name, string? FilePath, string? PartNumber);

/// <summary>
/// The coordinate frame a stock export is written in: the NC procedure UCS (mode A, Cimatron <c>cmNcProcUcs</c>) or
/// the part-model coordinates (mode B, <c>cmNcModelUcs</c>). It must be the frame the fixtures and the part are in.
/// </summary>
internal enum CimatronUcsFrame
{
    Procedure,
    Model
}

internal sealed record CimatronProcedure(int Id, string Name, string? UcsName, bool HasToolpath, string? OperationName = null);

/// <summary>
/// A Cimatron toolpath (one machine program) with its setup and the stock-defining procedure of that setup (null when
/// the setup has none or several). A package holds one toolpath.
/// </summary>
internal sealed record CimatronToolpath(int Id, string Name, string? SetupName, int? StockProcedureId);

/// <summary>A UCS as a row-major 4×4 matrix mapping UCS coordinates into part-model coordinates (mm).</summary>
internal sealed record CimatronUcs(string Name, IReadOnlyList<double> Matrix);

internal sealed record CimatronTool(int Number, string Name, string? Type, double? DiameterMm, double? CornerRadiusMm,
    double? FluteLengthMm, double? GaugeLengthMm, string? HolderName, double? HolderDiameterMm, double? HolderLengthMm);

internal static class GeometryRoles
{
    internal const string Target = "target";
    internal const string Fixture = "fixture";
    internal const string Other = "other";
}

/// <summary>One mesh the geometry export wrote, with the role Cimatron gave it (target/fixture/other).</summary>
internal sealed record CimatronGeometryFile(string Path, string Role, string? Name, string Method);

/// <summary>A Cimatron API failure with a stable code for the export log.</summary>
internal sealed class CimatronApiException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}

/// <summary>
/// The Cimatron operations the exporter needs (exporter definition §3). The real implementation is
/// <see cref="CimatronComApi"/> (late-bound COM); tests use a fake. Every export method writes into the
/// folder or file it is given (the exporter's staging folder) and returns what it actually wrote.
/// </summary>
internal interface ICimatronApi : IDisposable
{
    string? CimatronVersion { get; }

    string AdapterVersion { get; }

    CimatronCapabilities Capabilities { get; }

    /// <summary>The active NC document; throws <see cref="CimatronApiException"/> when none is open.</summary>
    CimatronDocumentInfo GetActiveNcDocument();

    IReadOnlyList<CimatronProcedure> GetProcedures();

    /// <summary>The toolpaths of the NC document (empty when the API cannot tell).</summary>
    IReadOnlyList<CimatronToolpath> GetToolpaths();

    IReadOnlyList<string> GetUcsNames();

    CimatronUcs? GetUcs(string name);

    IReadOnlyList<CimatronTool> GetTools();

    /// <summary>Posts every NC procedure relative to <paramref name="ucsName"/>; returns the NC files created.</summary>
    IReadOnlyList<string> PostByUcs(string postName, string outputFolder, bool startHome, string ucsName);

    /// <summary>
    /// Posts only the given nodes (a toolpath id, or procedure ids) relative to <paramref name="ucsName"/>
    /// (<c>PostByUcsByIdVec</c>) into a file named <paramref name="outputName"/>; returns the NC files created.
    /// </summary>
    IReadOnlyList<string> PostSelected(string postName, string outputFolder, IReadOnlyList<int> nodeIds, string ucsName, string outputName);

    /// <summary>Fallback post of every NC procedure (the procedure UCS is still recorded by the exporter).</summary>
    IReadOnlyList<string> PostToAll(string postName, string outputFolder, bool startHome);

    void ExportCuttersAndHolders(string cuttersCsv, string holdersCsv, string materialsCsv);

    void ExportJobsData(string xmlPath);

    /// <summary>Writes the remaining stock after <paramref name="procedureId"/> as STL in <paramref name="frame"/>.</summary>
    void ExportRemainingStock(int procedureId, CimatronUcsFrame frame, string stlPath);

    void ExportInitialStock(CimatronUcsFrame frame, string stlPath);

    /// <summary>Exports target, fixture and other parts to separate meshes in <paramref name="folder"/>.</summary>
    IReadOnlyList<CimatronGeometryFile> ExportPartGeometry(string ucsName, string folder);
}
