using System.Collections.ObjectModel;
using System.IO;
using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Presentation.NcViewer;
using Meimad.Planner.NcEngine.MachineDefinitions;

namespace Meimad.Planner.Client.Windows.Presentation.MachineDesigner;

/// <summary>A factory Machine the designer can copy its definition from, shown as "number — name".</summary>
internal sealed record DesignerSourceMachine(string MachineId, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// "Copy from Machine": starts this Machine's draft from another Machine's definition (owner request 2026-10-14) — its
/// components, joints, mounts, axis names, NC viewer machine, magazine, timing and collision settings, with every
/// uploaded mesh copied into this Machine's asset store (assets belong to one Machine on the Server). This Machine's
/// name is kept and the calibration is cleared: it was verified on the other machine. Nothing is saved until the
/// planner saves the draft.
/// </summary>
internal sealed partial class MachineDesignerViewModel
{
    private DesignerSourceMachine? selectedSourceMachine;
    private AsyncCommand? copyFromMachineCommand;

    public ObservableCollection<DesignerSourceMachine> SourceMachines { get; } = [];

    public DesignerSourceMachine? SelectedSourceMachine
    {
        get => selectedSourceMachine;
        set
        {
            if (Set(ref selectedSourceMachine, value)) CopyFromMachineCommand.RaiseCanExecuteChanged();
        }
    }

    public AsyncCommand CopyFromMachineCommand => copyFromMachineCommand ??=
        new AsyncCommand(CopyFromMachineAsync, () => CanEdit && !IsBusy && SelectedSourceMachine is not null);

    /// <summary>The other Machines, by number; a failure leaves the list empty (the copy is then unavailable).</summary>
    internal async Task LoadSourceMachinesAsync()
    {
        try
        {
            var machines = await api.ListMachinesAsync();
            SourceMachines.Clear();
            foreach (var machine in machines
                         .Where(machine => machine.MachineId != Machine.MachineId)
                         .OrderBy(machine => machine.Number, StringComparer.OrdinalIgnoreCase))
            {
                SourceMachines.Add(new DesignerSourceMachine(machine.MachineId, $"{machine.Number} — {machine.Name}"));
            }
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            SourceMachines.Clear();
        }
    }

    internal async Task CopyFromMachineAsync()
    {
        if (SelectedSourceMachine is not { } source) return;
        if (!host.Confirm(
                $"Replace this Machine's components, mounts, meshes, axis names, magazine and timing with the draft of {source.Label}? " +
                "The name is kept and the calibration is cleared. Check every position against this machine before you publish.",
                "Copy from Machine"))
            return;
        IsBusy = true;
        try
        {
            var definition = (await api.GetMachineSimulationAsync(source.MachineId)).Value.Draft;
            if (definition.Nodes.Count == 0)
            {
                StatusText = $"{source.Label} has no components to copy.";
                return;
            }
            var (copied, missing) = await CopyMeshesAsync(source.MachineId, definition);
            var current = BuildDefinition(out _);
            Populate(copied with
            {
                MachineId = Machine.MachineId,
                Name = current.Name,
                Calibration = null
            });
            IsDirty = true;
            var meshCount = copied.Nodes.Sum(node => node.Models.Count(model => model.AssetId is not null));
            StatusText = $"Copied from {source.Label}: {copied.Nodes.Count} components, {meshCount} meshes" +
                         (missing > 0 ? $" ({missing} meshes could not be copied and were left out)" : string.Empty) +
                         ". Check every position against this machine, then save and publish.";
            await ResolveAssetsAsync();
        }
        catch (Exception exception) when (IsExpected(exception) || exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            StatusText = $"Nothing was copied from {source.Label}: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// The definition with each uploaded mesh of <paramref name="sourceMachineId"/> uploaded again for this Machine (the
    /// Server keeps assets per Machine); a mesh that cannot be read is left out and counted. File-path meshes from
    /// schema v113 stay as they are.
    /// </summary>
    private async Task<(MachineSimulationDefinition Definition, int Missing)> CopyMeshesAsync(
        string sourceMachineId, MachineSimulationDefinition definition)
    {
        var used = definition.Nodes.SelectMany(node => node.Models).Select(model => model.AssetId).OfType<string>().Distinct().ToArray();
        var copies = new Dictionary<string, string>(StringComparer.Ordinal);
        var missing = 0;
        if (used.Length > 0)
        {
            var assets = await api.ListMachineSimulationAssetsAsync(sourceMachineId);
            foreach (var assetId in used)
            {
                if (assets.FirstOrDefault(asset => asset.AssetId == assetId) is not { } asset)
                {
                    missing++;
                    continue;
                }
                var path = await NcViewerMachineDefinitions.CachedAsync(api, sourceMachineId, asset, CancellationToken.None);
                var bytes = await File.ReadAllBytesAsync(path);
                using var stream = new MemoryStream(bytes, writable: false);
                var copy = await api.UploadMachineSimulationAssetAsync(Machine.MachineId, asset.FileName, stream, clientId);
                await RememberAsync(copy, bytes);
                copies[assetId] = copy.AssetId;
            }
        }
        var nodes = definition.Nodes.Select(node => node with
        {
            Models = node.Models
                .Where(model => model.AssetId is null || copies.ContainsKey(model.AssetId))
                .Select(model => model.AssetId is { } id ? model with { AssetId = copies[id] } : model)
                .ToArray()
        }).ToArray();
        return (definition with { Nodes = nodes }, missing);
    }
}
