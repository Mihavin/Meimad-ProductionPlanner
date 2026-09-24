using System.IO;
using Meimad.Planner.NcEngine;

namespace Meimad.Planner.Client.Windows.Presentation;

/// <summary>
/// One choice in the Setup "NC viewer machine" list: an NC engine machine definition installed
/// with this client (the same catalog the Server validates against), or automatic detection.
/// </summary>
/// <param name="Id">Engine machine id (for example <c>haas-vf-3ss</c>); null for auto-detect.</param>
/// <param name="Name">Display text.</param>
internal sealed record NcViewerMachineOption(string? Id, string Name)
{
    internal static readonly NcViewerMachineOption Auto = new(null, "Auto-detect from the program");

    /// <summary>Auto-detect first, then the installed definitions by name.</summary>
    internal static IEnumerable<NcViewerMachineOption> Installed(string? engineRoot = null)
    {
        yield return Auto;
        IReadOnlyList<NcEngineMachineDefinition> machines;
        try
        {
            machines = NcEngineMachineCatalog.Load(engineRoot).Machines;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            yield break;
        }
        foreach (var machine in machines)
        {
            yield return new NcViewerMachineOption(
                machine.Id,
                $"{machine.Name} — {(machine.Type == "lathe" ? "lathe" : "mill")}, {machine.Control}");
        }
    }

    public override string ToString() => Name;
}
