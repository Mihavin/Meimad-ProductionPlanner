using System.IO;
using System.Net.Http;
using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Views;

namespace Meimad.Planner.Client.Windows.Presentation.NcViewer;

/// <summary>
/// A fixture or rest-material file of an Operation's Cimatron setup, in part-model coordinates.
/// <paramref name="Kind"/> is "fixture" or "stock" (rest material).
/// </summary>
internal sealed record NcViewerSceneFile(string Name, string Path, string Kind);

/// <summary>
/// What the NC viewer draws around the toolpath: the Machine 3D models of every NC viewer machine
/// (keyed by viewer machine id, so the page's own machine choice picks the matching set), and the
/// opened Operation's Cimatron Work CS with its fixture and rest-material files.
/// <paramref name="CompiledMachines"/> are the Machines' published Machine Designer definitions
/// (schema v114) compiled for the engine; their meshes are in <paramref name="MachineModels"/>
/// under the compiled machine id.
/// </summary>
internal sealed record NcViewerSetupScene(
    string? OperationTitle,
    CaseOperationCamSetup? WorkCs,
    IReadOnlyList<NcViewerSceneFile> Files,
    IReadOnlyDictionary<string, IReadOnlyList<NcViewerMachineModel>> MachineModels,
    IReadOnlyList<NcViewerCompiledMachine>? CompiledMachines = null,
    NcViewerOperationCard? Card = null)
{
    internal static NcViewerSetupScene Empty { get; } = new(null, null, [], new Dictionary<string, IReadOnlyList<NcViewerMachineModel>>());

    internal IReadOnlyList<NcViewerCompiledMachine> Compiled => CompiledMachines ?? [];

    internal bool IsEmpty => WorkCs is null && Files.Count == 0 && MachineModels.Count == 0 && Compiled.Count == 0;
}

/// <summary>Builds the NC viewer's setup scene from Server data.</summary>
internal static class NcViewerScenes
{
    /// <summary>
    /// The 3D models per NC viewer machine. When several Meimad Machines share one viewer machine,
    /// the assigned Machine's models win, then the lowest Machine number that has models.
    /// </summary>
    internal static IReadOnlyDictionary<string, IReadOnlyList<NcViewerMachineModel>> MachineModels(
        IReadOnlyList<PlannerMachine> machines, string? assignedMachineId)
    {
        var result = new Dictionary<string, IReadOnlyList<NcViewerMachineModel>>(StringComparer.Ordinal);
        var ordered = machines
            .Where(machine => !string.IsNullOrWhiteSpace(machine.NcViewerMachine) && machine.NcViewerModels is { Count: > 0 })
            .OrderBy(machine => machine.MachineId == assignedMachineId ? 0 : 1)
            .ThenBy(machine => machine.Number, StringComparer.OrdinalIgnoreCase);
        foreach (var machine in ordered)
        {
            result.TryAdd(machine.NcViewerMachine!.Trim(), machine.NcViewerModels!);
        }
        return result;
    }

    /// <summary>
    /// The Operation's Cimatron setup and its fixture and rest-material files. The scene is optional:
    /// an offline Server or a missing setup gives the parts that could be read.
    /// </summary>
    internal static async Task<NcViewerSetupScene> ForOperationAsync(
        IPlannerApiClient api,
        string caseId,
        string caseOperationId,
        string? machineId,
        IReadOnlyList<PlannerMachine> machines,
        string? operationTitle = null,
        CancellationToken cancellationToken = default)
    {
        CaseOperationCamSetup? workCs = null;
        IReadOnlyList<NcViewerSceneFile> files = [];
        try
        {
            workCs = (await api.GetCaseOperationCamSetupAsync(caseId, caseOperationId, cancellationToken))?.Value;
        }
        catch (Exception exception) when (IsTransient(exception))
        {
            // Older Server or offline: the files are drawn at their own coordinates.
        }
        try
        {
            files = OperationFiles(await api.ListCaseModelFilesAsync(caseId, cancellationToken), caseOperationId);
        }
        catch (Exception exception) when (IsTransient(exception))
        {
            // No files: the machine models are still drawn.
        }
        var compiled = await NcViewerMachineDefinitions.LoadAsync(api, cancellationToken: cancellationToken);
        var card = await CardAsync(api, caseId, caseOperationId, machineId, machines, cancellationToken);
        return new NcViewerSetupScene(operationTitle, workCs, files, WithCompiledModels(MachineModels(machines, machineId), compiled), compiled, card);
    }

    /// <summary>
    /// The viewer's operator card: Part Number, Case name, Operation, Machine and the Case picture (the stored
    /// picture, else the Case's picture link, as the Case workspace shows it). Parts the Server cannot give stay empty.
    /// </summary>
    internal static async Task<NcViewerOperationCard> CardAsync(
        IPlannerApiClient api,
        string caseId,
        string caseOperationId,
        string? machineId,
        IReadOnlyList<PlannerMachine> machines,
        CancellationToken cancellationToken = default)
    {
        PlannerCase? plannerCase = null;
        CaseOperation? operation = null;
        string? picture = null;
        try
        {
            plannerCase = (await api.GetCaseAsync(caseId, cancellationToken)).Value;
        }
        catch (Exception exception) when (IsTransient(exception))
        {
        }
        try
        {
            operation = (await api.ListCaseOperationsAsync(caseId, cancellationToken))
                .FirstOrDefault(candidate => candidate.CaseOperationId == caseOperationId);
        }
        catch (Exception exception) when (IsTransient(exception))
        {
        }
        try
        {
            picture = PictureDataUrl(await api.GetCasePreviewAsync(caseId, cancellationToken));
        }
        catch (Exception exception) when (IsTransient(exception))
        {
        }
        var machine = machineId is null ? null : machines.FirstOrDefault(candidate => candidate.MachineId == machineId);
        return new NcViewerOperationCard(
            plannerCase?.PartNumber, plannerCase?.Name, operation?.OperationNumber, operation?.Name,
            machine is null ? null : $"{machine.Number} {machine.Name}".Trim(), picture);
    }

    /// <summary>A PNG, JPEG, GIF or BMP picture as a data URL for the viewer page (at most 4 MB; others are left out).</summary>
    internal static string? PictureDataUrl(byte[]? bytes)
    {
        if (bytes is null || bytes.Length < 8 || bytes.Length > 4 * 1024 * 1024) return null;
        var mediaType = bytes[0] == 0x89 && bytes[1] == 0x50 ? "image/png"
            : bytes[0] == 0xFF && bytes[1] == 0xD8 ? "image/jpeg"
            : bytes[0] == (byte)'G' && bytes[1] == (byte)'I' ? "image/gif"
            : bytes[0] == (byte)'B' && bytes[1] == (byte)'M' ? "image/bmp"
            : null;
        return mediaType is null ? null : $"data:{mediaType};base64,{Convert.ToBase64String(bytes)}";
    }

    /// <summary>The template-keyed models plus each compiled Machine's meshes under its compiled id.</summary>
    internal static IReadOnlyDictionary<string, IReadOnlyList<NcViewerMachineModel>> WithCompiledModels(
        IReadOnlyDictionary<string, IReadOnlyList<NcViewerMachineModel>> models, IReadOnlyList<NcViewerCompiledMachine> compiled)
    {
        var result = new Dictionary<string, IReadOnlyList<NcViewerMachineModel>>(models, StringComparer.Ordinal);
        foreach (var machine in compiled.Where(machine => machine.Models.Count > 0)) result[machine.CompiledId] = machine.Models;
        return result;
    }

    /// <summary>
    /// The fixture, rest-material ("stock") and finished-part ("part", linked to the Operation by a Cimatron
    /// package import) files of one Operation, in list order. Case-level part models stay out of the scene.
    /// </summary>
    internal static IReadOnlyList<NcViewerSceneFile> OperationFiles(IReadOnlyList<CaseModelFile> files, string caseOperationId) =>
        files
            .Where(file => file.CaseOperationId == caseOperationId && file.Kind is "fixture" or "stock" or "part")
            .OrderBy(file => file.Kind switch { "fixture" => 0, "stock" => 1, _ => 2 })
            .ThenBy(file => file.SortOrder)
            .ThenBy(file => file.Label, StringComparer.CurrentCultureIgnoreCase)
            .Select(file => new NcViewerSceneFile(
                string.IsNullOrWhiteSpace(file.Label) ? Path.GetFileName(file.FilePath) : file.Label,
                file.FilePath,
                file.Kind))
            .ToArray();

    /// <summary>
    /// Whether an imported Cimatron file is rest material: Cimatron writes stock exports as
    /// <c>STOCK_OP1.stl</c> or into an <c>NC\STOCK</c> folder. Everything else is a fixture.
    /// Mirrors guessKind in meimad-scene-core.js.
    /// </summary>
    internal static string GuessKind(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();
        var folder = Path.GetFileName(Path.GetDirectoryName(path) ?? string.Empty).ToLowerInvariant();
        return folder == "stock" || name.Contains("stock", StringComparison.Ordinal)
            || name.Contains("rest", StringComparison.Ordinal) || name.Contains("remain", StringComparison.Ordinal)
            ? "stock"
            : "fixture";
    }

    private static bool IsTransient(Exception exception) =>
        exception is HttpRequestException or TaskCanceledException or PlannerApiException or NotSupportedException;
}

/// <summary>
/// Meshes for the NC viewer page as binary STL: an STL file as stored (the page reads binary and
/// ASCII STL), a STEP model tessellated with OpenCascade. Original files are only read.
/// </summary>
internal static class NcViewerMeshes
{
    internal const long MaximumFileBytes = 200L * 1024 * 1024;
    private const int MaximumTriangles = 4_000_000;

    internal static bool IsSupported(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".stl" or ".stp" or ".step" or ".glb";

    internal static byte[] ReadAsStl(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new FileNotFoundException("The model file was not found.", path);
        if (!IsSupported(path)) throw new InvalidDataException("Only STL, STEP and GLB models can be shown.");
        if (new FileInfo(path).Length > MaximumFileBytes) throw new InvalidDataException("The model file is larger than 200 MB.");
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".stl" => File.ReadAllBytes(path),
            ".glb" => GlbMeshReader.ReadAsStl(File.ReadAllBytes(path)),
            _ => ToBinaryStl(StepSolidMeshLoader.LoadMesh(path))
        };
    }

    /// <summary>Binary STL of a tessellated model (normals left zero; the page recomputes them).</summary>
    internal static byte[] ToBinaryStl(StepModelData mesh)
    {
        var triangles = mesh.Triangles;
        if (triangles.Count == 0) throw new InvalidDataException("The model has no surface triangles.");
        if (triangles.Count > MaximumTriangles) throw new InvalidDataException("The model has too many triangles to show.");
        var bytes = new byte[84 + triangles.Count * 50];
        var header = "Meimad Planner NC viewer mesh"u8;
        header.CopyTo(bytes);
        BitConverter.TryWriteBytes(bytes.AsSpan(80, 4), (uint)triangles.Count);
        var offset = 84;
        foreach (var triangle in triangles)
        {
            offset += 12; // normal
            foreach (var index in (ReadOnlySpan<int>)[triangle.FirstIndex, triangle.SecondIndex, triangle.ThirdIndex])
            {
                var point = mesh.Points[index];
                BitConverter.TryWriteBytes(bytes.AsSpan(offset, 4), (float)point.X);
                BitConverter.TryWriteBytes(bytes.AsSpan(offset + 4, 4), (float)point.Y);
                BitConverter.TryWriteBytes(bytes.AsSpan(offset + 8, 4), (float)point.Z);
                offset += 12;
            }
            offset += 2; // attribute byte count
        }
        return bytes;
    }
}

/// <summary>What the NC viewer's operator card shows about the opened Operation (any part may be missing).</summary>
internal sealed record NcViewerOperationCard(
    string? PartNumber,
    string? CaseName,
    int? OperationNumber,
    string? OperationName,
    string? MachineName,
    string? PictureDataUrl);
