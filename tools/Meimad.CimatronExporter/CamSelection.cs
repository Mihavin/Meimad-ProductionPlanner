using Meimad.Planner.SimulationPackage.Manifest;

namespace Meimad.CimatronExporter;

/// <summary>
/// CAM operations chosen by Cimatron id for one Meimad import (Meimad import specification §3): whole toolpaths and single
/// procedures of one setup. <see cref="NodeIds"/> are the process-manager nodes posted with PostByUcsByIdVec (a whole toolpath
/// as its own node, other procedures one by one); <see cref="StockToolpath"/> carries the setup's stock procedure.
/// </summary>
internal sealed record CamSelection(
    IReadOnlyList<int> NodeIds,
    IReadOnlyList<CimatronProcedure> Procedures,
    CimatronToolpath StockToolpath,
    string Name,
    string? SetupName,
    IReadOnlyList<CamOperationReference> Operations)
{
    public static CamSelection? From(ExportOptions options, IReadOnlyList<CimatronToolpath> toolpaths, IReadOnlyList<CimatronProcedure> procedures,
        ExportLog log)
    {
        var toolpathIds = (options.ToolpathIds ?? []).Distinct().ToArray();
        var procedureIds = (options.ProcedureIds ?? []).Distinct().ToArray();
        var missing = toolpathIds.Where(id => toolpaths.All(item => item.Id != id)).Select(id => $"toolpath {id}")
            .Concat(procedureIds.Where(id => procedures.All(item => item.Id != id)).Select(id => $"procedure {id}"))
            .ToArray();
        if (missing.Length > 0)
        {
            log.Error("state", "selection_not_found",
                $"The chosen CAM operations are no longer in the Cimatron document ({string.Join(", ", missing)}); choose them again.");
            return null;
        }

        CimatronToolpath? ToolpathOf(CimatronProcedure procedure) =>
            toolpaths.FirstOrDefault(item => string.Equals(item.Name, procedure.OperationName, StringComparison.OrdinalIgnoreCase));

        var wholeToolpaths = toolpaths.Where(item => toolpathIds.Contains(item.Id)).ToArray();
        var chosen = procedures
            .Where(procedure => procedureIds.Contains(procedure.Id)
                                || wholeToolpaths.Any(item => string.Equals(item.Name, procedure.OperationName, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (chosen.Length == 0)
        {
            log.Error("state", "selection_empty", "The chosen CAM operations hold no procedure to post.");
            return null;
        }

        var involved = wholeToolpaths.Concat(chosen.Select(ToolpathOf).OfType<CimatronToolpath>())
            .GroupBy(item => item.Id).Select(group => group.First()).ToArray();
        var setups = involved.Select(item => item.SetupName ?? "").Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (setups.Length > 1)
        {
            log.Error("state", "selection_spans_setups",
                $"The chosen CAM operations belong to the setups {string.Join(", ", setups.Select(name => $"'{name}'"))}; one import holds one setup (its stock and work frame).");
            return null;
        }

        // Whole toolpaths post as their node; any other chosen procedure posts by itself.
        var nodes = wholeToolpaths.Select(item => item.Id)
            .Concat(chosen.Where(procedure => wholeToolpaths.All(item => !string.Equals(item.Name, procedure.OperationName, StringComparison.OrdinalIgnoreCase)))
                .Select(procedure => procedure.Id))
            .ToArray();
        var operations = wholeToolpaths.Select(item => new CamOperationReference { Kind = "toolpath", Id = item.Id, Name = item.Name })
            .Concat(chosen.Where(procedure => !nodes.Contains(ToolpathOf(procedure)?.Id ?? -1) && nodes.Contains(procedure.Id))
                .Select(procedure => new CamOperationReference { Kind = "procedure", Id = procedure.Id, Name = procedure.Name, Toolpath = procedure.OperationName }))
            .ToArray();
        var stock = involved.FirstOrDefault(item => item.StockProcedureId is not null) ?? involved.FirstOrDefault()
            ?? new CimatronToolpath(-1, chosen[0].OperationName ?? "selection", null, null);
        var name = involved.Length == 1 ? involved[0].Name : string.Join("+", involved.Select(item => item.Name).Take(4));
        return new CamSelection(nodes, chosen, stock, name, involved.FirstOrDefault()?.SetupName, operations);
    }
}
