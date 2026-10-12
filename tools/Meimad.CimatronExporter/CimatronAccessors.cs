using System.Globalization;
using System.Xml.Linq;

namespace Meimad.CimatronExporter;

/// <summary>
/// What the process-manager XML of an NC document said: its procedures, the UCS names it mentions, and the procedures
/// that define stock (outside any toolpath, e.g. Cimatron's "Stock By Contours").
/// </summary>
internal sealed record ProcessManagerData(IReadOnlyList<CimatronProcedure> Procedures, IReadOnlyList<string> UcsNames,
    IReadOnlyList<int>? StockProcedureIds = null, IReadOnlyList<int>? NonMachiningIds = null)
{
    internal IReadOnlyList<int> StockProcedures => StockProcedureIds ?? [];

    /// <summary>Procedures outside every toolpath (target part, stock): not NC, left out of the export.</summary>
    internal IReadOnlyList<int> NonMachining => NonMachiningIds ?? [];

    /// <summary>The procedures that machine: those inside a toolpath.</summary>
    internal IReadOnlyList<CimatronProcedure> Machining => Procedures.Where(item => !NonMachining.Contains(item.Id)).ToArray();
}

/// <summary>
/// Reads Cimatron's process manager XML (<c>INcModel.GetProcessManagerAsXML</c>). Cimatron 2026.0 at Meimad writes an
/// <c>NCFullReport</c>: <c>Setup</c> → <c>SetupFolder</c> → <c>ToolPath Name=…</c> (with <c>ToolPath_UCS Value=…</c>) →
/// <c>ToolpathFolder</c> → <c>Procedure Name=… ID=…</c>, where the procedure UCS is <c>UCS_Name Value=…</c> under its
/// <c>GridParameters</c> and parameter rows are elements such as <c>Procedure_Status Value=…</c>. Procedures directly in
/// the setup folder (target part, stock) have no toolpath. Other layouts are still matched by meaning (an element named
/// like a procedure with an id-like attribute, a UCS in an attribute, a child element or a parameter row).
/// </summary>
internal static class ProcessManagerXml
{
    private static readonly string[] IdNames = ["uid", "id", "procid", "procuid", "itemid", "nodeid", "procedureid"];
    private static readonly string[] NameNames = ["name", "procname", "procedurename", "title", "caption"];
    private static readonly string[] ParameterNameNames = ["name", "paramname", "key", "id"];
    private static readonly string[] ParameterValueNames = ["value", "val", "text"];
    private static readonly string[] ToolpathNames = ["calculated", "iscalculated", "executed", "hastoolpath", "status", "state"];

    internal static ProcessManagerData Parse(string xml)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(xml, LoadOptions.None);
        }
        catch (System.Xml.XmlException)
        {
            return new ProcessManagerData([], []);
        }

        var procedures = new List<CimatronProcedure>();
        var ucsNames = new List<string>();
        var stock = new List<int>();
        var nonMachining = new List<int>();
        var seen = new HashSet<int>();
        var hasToolpaths = document.Descendants().Any(IsToolpath);
        foreach (var element in document.Descendants())
        {
            var local = element.Name.LocalName;
            if (local.Contains("ucs", StringComparison.OrdinalIgnoreCase) && !local.Contains("proc", StringComparison.OrdinalIgnoreCase))
            {
                var ucsName = UcsValue(element);
                if (ucsName is not null) ucsNames.Add(ucsName);
                continue;
            }

            if (!IsProcedure(element)) continue;
            var id = Int(Attribute(element, IdNames));
            if (id is null || !seen.Add(id.Value)) continue;
            var name = Attribute(element, NameNames) ?? $"Procedure {id}";
            var toolpath = element.Ancestors().FirstOrDefault(IsToolpath);
            var ucs = Ucs(element) ?? (toolpath is null ? null : ToolpathUcs(toolpath));
            if (ucs is not null) ucsNames.Add(ucs);
            var machining = !hasToolpaths || toolpath is not null;
            if (!machining) nonMachining.Add(id.Value);
            if (!machining && IsStockDefinition(element, name)) stock.Add(id.Value);
            procedures.Add(new CimatronProcedure(id.Value, name, ucs, machining && HasToolpath(element), OperationOf(element)));
        }

        return new ProcessManagerData(procedures, ucsNames.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), stock, nonMachining);
    }

    // An element that is one procedure: its name says so (a single word such as "Procedure"; parameter rows such as
    // "Procedure_Comment" and containers such as "Procedures" or "ProcedureParameters" are not), or its type attribute does.
    private static bool IsProcedure(XElement element)
    {
        var local = element.Name.LocalName;
        bool named;
        if (local.Equals("Item", StringComparison.OrdinalIgnoreCase) || local.Equals("Node", StringComparison.OrdinalIgnoreCase))
        {
            named = TypeSaysProcedure(element);
        }
        else
        {
            named = local.Contains("proc", StringComparison.OrdinalIgnoreCase) && !local.Contains('_')
                && !local.EndsWith("s", StringComparison.OrdinalIgnoreCase);
            if (!named) named = TypeSaysProcedure(element);
        }

        return named && Attribute(element, IdNames) is not null;
    }

    private static bool TypeSaysProcedure(XElement element)
    {
        var type = Attribute(element, ["type", "itemtype", "kind", "nctype"]);
        return type is not null && type.Contains("proc", StringComparison.OrdinalIgnoreCase);
    }

    // A toolpath (Cimatron's operation level): "ToolPath Name=…", not its folders or parameter blocks.
    private static bool IsToolpath(XElement element)
    {
        var local = element.Name.LocalName;
        return local.Contains("toolpath", StringComparison.OrdinalIgnoreCase) && !local.Contains('_')
            && !local.EndsWith("Folder", StringComparison.OrdinalIgnoreCase) && !local.EndsWith("Parameters", StringComparison.OrdinalIgnoreCase)
            && Attribute(element, NameNames) is not null;
    }

    // A stock-defining procedure outside the toolpaths: a Stock_Type parameter, or a name such as "Stock By Contours_3".
    private static bool IsStockDefinition(XElement element, string name) =>
        name.StartsWith("Stock", StringComparison.OrdinalIgnoreCase)
        || element.Descendants().Any(row => row.Name.LocalName.Equals("Stock_Type", StringComparison.OrdinalIgnoreCase));

    // The value an element mentioning a UCS carries: a name attribute, a Value attribute (not the "Access" button of
    // "Create_UCS", not an empty group row such as "Clearance___UCS"), or its text.
    private static string? UcsValue(XElement element)
    {
        if (element.Name.LocalName.StartsWith("Create", StringComparison.OrdinalIgnoreCase)) return null;
        var value = Attribute(element, NameNames) ?? Attribute(element, ParameterValueNames) ?? Text(element);
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        return value.Equals("Access", StringComparison.OrdinalIgnoreCase) ? null : value;
    }

    private static string? Ucs(XElement element)
    {
        foreach (var attribute in element.Attributes())
        {
            if (attribute.Name.LocalName.Contains("ucs", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(attribute.Value))
                return attribute.Value.Trim();
        }

        // Cimatron's own rows: <UCS_Name Value="…"/> (preferred) or another UCS-named row below the procedure.
        var rows = element.Descendants().Where(row => row.Name.LocalName.Contains("ucs", StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var row in rows.Where(row => row.Name.LocalName.Equals("UCS_Name", StringComparison.OrdinalIgnoreCase)))
        {
            if (UcsValue(row) is { } value) return value;
        }

        foreach (var row in rows)
        {
            if (UcsValue(row) is { } value) return value;
        }

        // A parameter row: <Parameter Name="UCS" Value="UCS_OP010"/> anywhere below the procedure.
        foreach (var row in element.Descendants())
        {
            var parameterName = Attribute(row, ParameterNameNames);
            if (parameterName is null || !parameterName.Contains("ucs", StringComparison.OrdinalIgnoreCase)) continue;
            var value = Attribute(row, ParameterValueNames) ?? Text(row);
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }

        return null;
    }

    // The toolpath's own UCS (ToolPath_UCS in its parameter block), used for procedures that name none (e.g. Drill 5x).
    private static string? ToolpathUcs(XElement toolpath) =>
        toolpath.Elements()
            .Where(child => child.Name.LocalName.EndsWith("Parameters", StringComparison.OrdinalIgnoreCase))
            .SelectMany(child => child.Descendants())
            .Where(row => row.Name.LocalName.Contains("ucs", StringComparison.OrdinalIgnoreCase))
            .Select(UcsValue)
            .FirstOrDefault(value => value is not null);

    private static bool HasToolpath(XElement element)
    {
        var value = Attribute(element, ToolpathNames);
        if (value is null)
        {
            var status = element.Descendants()
                .FirstOrDefault(row => row.Name.LocalName.Equals("Procedure_Status", StringComparison.OrdinalIgnoreCase));
            value = status is null ? null : Attribute(status, ParameterValueNames);
        }

        if (value is null) return true;
        var text = value.Trim().ToLowerInvariant();
        if (text.Contains("succeeded")) return true;
        return text is not ("false" or "0" or "no" or "dirty" or "uncalculated" or "notcalculated")
            && !text.Contains("not calculated") && !text.Contains("fail");
    }

    // The nearest ancestor that looks like a toolpath, operation, setup or job gives the operation name.
    private static string? OperationOf(XElement element)
    {
        foreach (var ancestor in element.Ancestors())
        {
            var local = ancestor.Name.LocalName;
            if (local.Contains("toolpath", StringComparison.OrdinalIgnoreCase) || local.Contains("oper", StringComparison.OrdinalIgnoreCase)
                || local.Contains("setup", StringComparison.OrdinalIgnoreCase) || local.Contains("job", StringComparison.OrdinalIgnoreCase)
                || local.Contains("program", StringComparison.OrdinalIgnoreCase))
            {
                var name = Attribute(ancestor, NameNames);
                if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
            }
        }

        return null;
    }

    private static string? Attribute(XElement element, IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            var attribute = element.Attributes().FirstOrDefault(item => string.Equals(item.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));
            if (attribute is not null && !string.IsNullOrWhiteSpace(attribute.Value)) return attribute.Value;
        }

        return null;
    }

    private static string? Text(XElement element) =>
        element.HasElements ? null : string.IsNullOrWhiteSpace(element.Value) ? null : element.Value;

    private static int? Int(string? value) =>
        value is not null && int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;
}

/// <summary>
/// Builds the row-major 4×4 UCS→model matrix from what Cimatron's <c>IModelTransformation</c> returns (<c>Matrix</c>
/// and <c>Vector</c>, Cimatron 2026.0): 16 values, 12 values (3×4), or 9 values plus a 3-value translation. Whether
/// Cimatron stores the translation in the last column or the last row is not documented; a 16-value matrix with the
/// translation in the last row (and zeros in the last column) is transposed, and the choice is reported.
/// </summary>
internal static class UcsMatrix
{
    internal static (IReadOnlyList<double> Matrix, string Layout)? Compose(IReadOnlyList<double>? matrix, IReadOnlyList<double>? vector)
    {
        if (matrix is null) return null;
        var translation = vector is { Count: >= 3 } ? vector.Take(3).ToArray() : null;
        switch (matrix.Count)
        {
            case 16:
            {
                var lastColumn = new[] { matrix[3], matrix[7], matrix[11] };
                var lastRow = new[] { matrix[12], matrix[13], matrix[14] };
                if (lastColumn.All(IsZero) && !lastRow.All(IsZero) && IsOne(matrix[15]) && IsZero(matrix[3]))
                    return (Transpose(matrix), "16 values, translation in the last row: transposed");
                return (matrix.ToArray(), "16 values row-major");
            }
            case 12:
                return ([.. matrix, 0, 0, 0, 1], "12 values (3×4) row-major");
            case 9 when translation is not null:
                return ([
                    matrix[0], matrix[1], matrix[2], translation[0],
                    matrix[3], matrix[4], matrix[5], translation[1],
                    matrix[6], matrix[7], matrix[8], translation[2],
                    0, 0, 0, 1
                ], "3×3 matrix plus vector");
            case 9:
                return ([
                    matrix[0], matrix[1], matrix[2], 0,
                    matrix[3], matrix[4], matrix[5], 0,
                    matrix[6], matrix[7], matrix[8], 0,
                    0, 0, 0, 1
                ], "3×3 matrix, no vector: origin assumed at the model origin");
            default:
                return null;
        }
    }

    private static double[] Transpose(IReadOnlyList<double> values)
    {
        var result = new double[16];
        for (var row = 0; row < 4; row++)
            for (var column = 0; column < 4; column++)
                result[row * 4 + column] = values[column * 4 + row];
        return result;
    }

    private static bool IsZero(double value) => Math.Abs(value) < 1e-9;

    private static bool IsOne(double value) => Math.Abs(value - 1) < 1e-9;
}

/// <summary>
/// Sorts out what <c>INcModel.NcExportJobsData(iOutputPath, out oNumberOfJobs)</c> wrote: the exporter gives it a folder;
/// one XML file there becomes <c>metadata/jobs.xml</c>, several stay as <c>metadata/jobs/</c> (not referenced by the
/// manifest), and a file written at the folder path itself is taken as the XML.
/// </summary>
internal static class JobsExportFiles
{
    /// <summary>Returns true when <paramref name="xmlPath"/> now holds the jobs XML.</summary>
    internal static bool Normalize(string exportFolder, string xmlPath)
    {
        if (File.Exists(exportFolder))
        {
            File.Move(exportFolder, xmlPath, overwrite: true);
            return true;
        }

        if (!Directory.Exists(exportFolder)) return File.Exists(xmlPath);
        var files = Directory.GetFiles(exportFolder, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length == 0)
        {
            Directory.Delete(exportFolder, true);
            return File.Exists(xmlPath);
        }

        if (files.Length == 1 && !File.Exists(xmlPath))
        {
            File.Move(files[0], xmlPath);
            Directory.Delete(exportFolder, true);
            return true;
        }

        var kept = Path.Combine(Path.GetDirectoryName(xmlPath)!, "jobs");
        if (Directory.Exists(kept)) Directory.Delete(kept, true);
        Directory.Move(exportFolder, kept);
        return File.Exists(xmlPath);
    }
}

/// <summary>
/// Finds the stock STL Cimatron's own export left beside the NC document when the API offers no initial-stock export
/// (Cimatron 2026.0 has none): <c>STOCK*.stl</c> in the document folder, its <c>NC</c> subfolder, or the <c>NC</c>
/// folder the document folder sits in. Exactly one match
/// is usable (as a fallback, reported as a warning); several need <c>--stock-file</c>.
/// </summary>
internal static class StockFileDiscovery
{
    internal static IReadOnlyList<string> Find(string? documentPath)
    {
        if (string.IsNullOrWhiteSpace(documentPath)) return [];
        string? folder;
        try
        {
            folder = Path.GetDirectoryName(documentPath);
        }
        catch (ArgumentException)
        {
            return [];
        }

        if (folder is null || !Directory.Exists(folder)) return [];
        var candidates = new List<string>();
        var searches = new List<string> { folder, Path.Combine(folder, "NC") };
        // Meimad keeps the NC document in NC\<part>\ and Cimatron's stock export in NC\ itself.
        var parent = Directory.GetParent(folder);
        if (parent is not null && parent.Name.Equals("NC", StringComparison.OrdinalIgnoreCase)) searches.Add(parent.FullName);
        foreach (var search in searches)
        {
            if (!Directory.Exists(search)) continue;
            candidates.AddRange(Directory.EnumerateFiles(search, "*.stl")
                .Where(path => Path.GetFileName(path).StartsWith("STOCK", StringComparison.OrdinalIgnoreCase)));
        }

        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}

/// <summary>The bounding box and triangle count of a binary STL (Cimatron's remaining-stock export writes one).</summary>
internal static class StlBounds
{
    internal static (int Triangles, double[] Min, double[] Max)? Read(string path)
    {
        if (!File.Exists(path)) return null;
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 84) return null;
        var count = BitConverter.ToUInt32(bytes, 80);
        if (bytes.Length != 84 + 50L * count || count == 0) return null;
        double[] min = [double.MaxValue, double.MaxValue, double.MaxValue];
        double[] max = [double.MinValue, double.MinValue, double.MinValue];
        for (var triangle = 0; triangle < count; triangle++)
        {
            for (var vertex = 0; vertex < 3; vertex++)
            {
                var offset = 84 + triangle * 50 + 12 + vertex * 12;
                for (var axis = 0; axis < 3; axis++)
                {
                    double value = BitConverter.ToSingle(bytes, offset + axis * 4);
                    min[axis] = Math.Min(min[axis], value);
                    max[axis] = Math.Max(max[axis], value);
                }
            }
        }

        return ((int)count, min, max);
    }
}

/// <summary>
/// Decides how Cimatron's UCS <c>Matrix</c> is laid out by comparing the composed matrix of the active UCS with what
/// <c>IModel.Active2ModelTranslate</c> answers for the active origin and unit axes (the axes' model coordinates are the
/// columns of the UCS→model rotation). A symmetric rotation cannot tell rows from columns.
/// </summary>
internal static class UcsLayoutCheck
{
    internal static string Compare(IReadOnlyList<double> composed, IReadOnlyList<double> origin, IReadOnlyList<double> xPoint,
        IReadOnlyList<double> yPoint, IReadOnlyList<double> zPoint)
    {
        var columns = new[] { xPoint, yPoint, zPoint }.Select(point => Enumerable.Range(0, 3).Select(axis => point[axis] - origin[axis]).ToArray()).ToArray();
        double asComposed = 0, transposed = 0, symmetric = 0;
        for (var row = 0; row < 3; row++)
        {
            asComposed = Math.Max(asComposed, Math.Abs(composed[row * 4 + 3] - origin[row]));
            for (var column = 0; column < 3; column++)
            {
                asComposed = Math.Max(asComposed, Math.Abs(composed[row * 4 + column] - columns[column][row]));
                transposed = Math.Max(transposed, Math.Abs(composed[column * 4 + row] - columns[column][row]));
                symmetric = Math.Max(symmetric, Math.Abs(composed[row * 4 + column] - composed[column * 4 + row]));
            }
        }

        transposed = Math.Max(transposed, Enumerable.Range(0, 3).Max(row => Math.Abs(composed[row * 4 + 3] - origin[row])));
        const double tolerance = 1e-4;
        if (asComposed < tolerance && symmetric < tolerance)
            return "matches as composed, but the rotation is symmetric: activate a tilted UCS (e.g. UCS11_3) in Cimatron and probe again to tell rows from columns";
        if (asComposed < tolerance) return "CONFIRMED: Matrix is row-major with the Vector as the UCS origin in model coordinates (as composed)";
        if (transposed < tolerance) return "the rotation must be TRANSPOSED (Matrix holds the UCS axes as rows)";
        return $"matches neither layout (largest difference {asComposed:G4} as composed, {transposed:G4} transposed)";
    }
}
