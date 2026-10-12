using System.Globalization;
using System.Xml.Linq;

namespace Meimad.CimatronExporter;

/// <summary>What the process-manager XML of an NC document said: its procedures and the UCS names it mentions.</summary>
internal sealed record ProcessManagerData(IReadOnlyList<CimatronProcedure> Procedures, IReadOnlyList<string> UcsNames);

/// <summary>
/// Reads Cimatron's process manager XML (<c>INcModel.GetProcessManagerAsXML</c>, Cimatron 2026.0) without knowing its
/// exact schema: the element and attribute names are matched by meaning (an element whose name mentions a procedure
/// with an id-like attribute; a name-like attribute; a UCS mentioned in an attribute, a child element or a parameter
/// row). The first real export at Meimad will show the actual layout; the probe prints both the file and what was
/// read from it so the rules can be tightened.
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
        var seen = new HashSet<int>();
        foreach (var element in document.Descendants())
        {
            var local = element.Name.LocalName;
            if (local.Contains("ucs", StringComparison.OrdinalIgnoreCase) && !local.Contains("proc", StringComparison.OrdinalIgnoreCase))
            {
                var ucsName = Attribute(element, NameNames) ?? Text(element);
                if (!string.IsNullOrWhiteSpace(ucsName)) ucsNames.Add(ucsName.Trim());
                continue;
            }

            if (!IsProcedure(element)) continue;
            var id = Int(Attribute(element, IdNames));
            if (id is null || !seen.Add(id.Value)) continue;
            var name = Attribute(element, NameNames) ?? $"Procedure {id}";
            var ucs = Ucs(element);
            if (ucs is not null) ucsNames.Add(ucs);
            procedures.Add(new CimatronProcedure(id.Value, name, ucs, HasToolpath(element), OperationOf(element)));
        }

        return new ProcessManagerData(procedures, ucsNames.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    // An element that is one procedure: its name says so, or its type attribute does; containers ("Procedures") and
    // rows without an id are skipped.
    private static bool IsProcedure(XElement element)
    {
        var local = element.Name.LocalName;
        var named = local.Contains("proc", StringComparison.OrdinalIgnoreCase) && !local.EndsWith("s", StringComparison.OrdinalIgnoreCase)
            || local.Equals("Item", StringComparison.OrdinalIgnoreCase) || local.Equals("Node", StringComparison.OrdinalIgnoreCase);
        if (!named)
        {
            var type = Attribute(element, ["type", "itemtype", "kind", "nctype"]);
            named = type is not null && type.Contains("proc", StringComparison.OrdinalIgnoreCase);
        }
        else if (local.Equals("Item", StringComparison.OrdinalIgnoreCase) || local.Equals("Node", StringComparison.OrdinalIgnoreCase))
        {
            var type = Attribute(element, ["type", "itemtype", "kind", "nctype"]);
            named = type is not null && type.Contains("proc", StringComparison.OrdinalIgnoreCase);
        }

        return named && Attribute(element, IdNames) is not null;
    }

    private static string? Ucs(XElement element)
    {
        foreach (var attribute in element.Attributes())
        {
            if (attribute.Name.LocalName.Contains("ucs", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(attribute.Value))
                return attribute.Value.Trim();
        }

        foreach (var child in element.Elements())
        {
            var local = child.Name.LocalName;
            if (local.Contains("ucs", StringComparison.OrdinalIgnoreCase))
            {
                var value = Attribute(child, NameNames) ?? Attribute(child, ParameterValueNames) ?? Text(child);
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            }
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

    private static bool HasToolpath(XElement element)
    {
        var value = Attribute(element, ToolpathNames);
        if (value is null) return true;
        var text = value.Trim().ToLowerInvariant();
        return text is not ("false" or "0" or "no" or "notcalculated" or "not calculated" or "dirty" or "uncalculated");
    }

    // The nearest ancestor that looks like an operation, setup or job gives the operation name.
    private static string? OperationOf(XElement element)
    {
        foreach (var ancestor in element.Ancestors())
        {
            var local = ancestor.Name.LocalName;
            if (local.Contains("oper", StringComparison.OrdinalIgnoreCase) || local.Contains("setup", StringComparison.OrdinalIgnoreCase)
                || local.Contains("job", StringComparison.OrdinalIgnoreCase) || local.Contains("program", StringComparison.OrdinalIgnoreCase))
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
/// (Cimatron 2026.0 has none): <c>STOCK*.stl</c> in the document folder or its <c>NC</c> subfolder. Exactly one match
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
        foreach (var search in new[] { folder, Path.Combine(folder, "NC") })
        {
            if (!Directory.Exists(search)) continue;
            candidates.AddRange(Directory.EnumerateFiles(search, "*.stl")
                .Where(path => Path.GetFileName(path).StartsWith("STOCK", StringComparison.OrdinalIgnoreCase)));
        }

        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
