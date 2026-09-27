using Meimad.Planner.NcEngine;
using Meimad.Planner.Server.Domain.GCode;

namespace Meimad.Planner.Server.Application.GCode;

/// <summary>Produces the machine-independent NC analysis stored for a G-code release.</summary>
internal interface INcProgramAnalyzer
{
    /// <summary>Parser version of analyses this analyzer produces when it succeeds.</summary>
    string CurrentVersion { get; }

    /// <param name="machines">NC interpretation (dialect, NC viewer machine) of the Machines that support the release's postprocessor.</param>
    Task<NcProgramAnalysis> AnalyzeAsync(
        string storedPath,
        IReadOnlyCollection<MachineNcInterpretation> machines,
        DateTimeOffset analyzedAt,
        CancellationToken cancellationToken);
}

/// <summary>
/// Analyzes a released NC program with the vendored Chevalier NC engine (the NC viewer's
/// interpreter): macros, subprogram calls inside the file, canned cycles, feed-per-revolution
/// with constant surface speed, inverse time and rotary feeds are simulated. The resulting feed
/// time, rapid distance, tool-change count and dwell become the release's analysis; each
/// Machine's configured rapid rate, tool-change time and time factor are applied afterwards by
/// <see cref="NcCycleTimeEstimator"/>, exactly as for the former parser. The engine machine is
/// the NC viewer machine shared by every supporting Machine (so the Server estimate and the
/// Windows NC viewer interpret the program the same way, including the Meimad dialect
/// translations); otherwise the engine detects it from the program and the shared NC dialect.
/// If the engine cannot run, the release falls back to <see cref="NcProgramParser"/> so it
/// keeps an estimate.
/// </summary>
internal sealed class NcEngineProgramAnalyzer : INcProgramAnalyzer, IDisposable
{
    private const int MaximumStoredMessages = 40;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ILogger<NcEngineProgramAnalyzer> logger;
    private readonly Func<NcEngineRuntime> runtimeFactory;
    private NcEngineRuntime? runtime;

    public NcEngineProgramAnalyzer(ILogger<NcEngineProgramAnalyzer> logger)
        : this(logger, () => new NcEngineRuntime(new NcEngineRuntimeOptions { CallTimeout = TimeSpan.FromMinutes(3) }))
    {
    }

    internal NcEngineProgramAnalyzer(ILogger<NcEngineProgramAnalyzer> logger, Func<NcEngineRuntime> runtimeFactory)
    {
        this.logger = logger;
        this.runtimeFactory = runtimeFactory;
    }

    public string CurrentVersion => NcEngineInfo.AnalysisVersion;

    public async Task<NcProgramAnalysis> AnalyzeAsync(
        string storedPath,
        IReadOnlyCollection<MachineNcInterpretation> machines,
        DateTimeOffset analyzedAt,
        CancellationToken cancellationToken)
    {
        string text;
        try
        {
            text = NcTextFile.Decode(await File.ReadAllBytesAsync(storedPath, cancellationToken)).Text;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidDataException)
        {
            return NcProgramParser.Unavailable(analyzedAt,
                $"Estimate unavailable: NC file could not be read ({exception.GetType().Name}).");
        }

        var dialect = SingleDialect(machines);
        var viewerMachine = SingleViewerMachine(machines);
        // Lathe subprogram calls are inlined from the release's own folder only (the Server has
        // no program-memory folders); the engine may read nothing else outside its files.
        var folder = Path.GetDirectoryName(Path.GetFullPath(storedPath));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var engine = Runtime();
            engine.SetReadableFolders([folder]);
            var result = await Task.Run(() => engine.Analyze(
                new NcEngineAnalysisRequest(
                    WithoutLegacyHookBlocks(text),
                    dialect,
                    viewerMachine ?? NcEngineInfo.AutoMachine,
                    DocumentDirectory: folder),
                cancellationToken),
                cancellationToken);
            return Map(result, dialect, machines, analyzedAt);
        }
        catch (Exception exception) when (exception is NcEngineException
            or Microsoft.ClearScript.ScriptEngineException)
        {
            logger.LogWarning(exception,
                "The NC engine could not analyze {StoredPath}; the basic NC parser estimate is used instead.",
                storedPath);
            DisposeFaultedRuntime();
            var fallback = NcProgramParser.Parse(text.Split('\n'), analyzedAt);
            return fallback with
            {
                Warnings = [$"NC engine unavailable ({Trim(exception.Message)}); basic parser estimate.", .. fallback.Warnings]
            };
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose()
    {
        runtime?.Dispose();
        gate.Dispose();
    }

    internal static NcProgramAnalysis Map(
        NcEngineAnalysis result,
        string? dialect,
        IReadOnlyCollection<MachineNcInterpretation> machines,
        DateTimeOffset analyzedAt)
    {
        var interpretedAs = $"Interpreted as {result.MachineName ?? "unknown machine"} ({result.Interpreter ?? "engine"}): {result.SelectionReason ?? "auto-detected"}.";
        var messages = new List<string> { interpretedAs };
        if (dialect is null && machines.Select(machine => NcDialects.Normalize(machine.NcDialect)).Distinct(StringComparer.Ordinal).Count() > 1)
        {
            messages.Add("Machines supporting this postprocessor use different NC dialects; the interpreter was detected from the program.");
        }
        if (machines.Any(machine => machine.NcViewerMachine is not null) && SingleViewerMachine(machines) is null)
        {
            messages.Add("Machines supporting this postprocessor are configured with different NC viewer machines; the engine machine was detected from the program.");
        }
        if (result.Subprograms is { Count: > 0 })
        {
            messages.Add($"Subprograms inlined from the release folder: {string.Join(", ", result.Subprograms)}.");
        }
        if (result.UnestimatedSegmentCount > 0)
        {
            messages.Add($"{result.UnestimatedSegmentCount} motion segment(s) have no usable feed or spindle speed and are not timed.");
        }
        messages.AddRange(result.Warnings);
        messages.AddRange(result.Errors.Select(value => $"Program issue: {value}"));

        var unsupported = new List<string>();
        if (result.UnestimatedSegmentCount > 0) unsupported.Add("UNTIMED_MOTION");
        if (result.ResourceLimited) unsupported.Add("ENGINE_RESOURCE_LIMIT");
        // Without the Okuma translation (an Okuma OSP viewer machine) OSP-only syntax is not simulated.
        if (string.Equals(dialect, NcDialects.OkumaOsp, StringComparison.Ordinal)
            && !string.Equals(result.Translation, "okuma-osp-lathe", StringComparison.Ordinal))
        {
            unsupported.Add("OKUMA_OSP_SYNTAX");
        }
        var noMotion = result.SegmentCount == 0 && result.ExecutedBlockCount > 0;
        if (noMotion) messages.Add("The program produced no motion; the estimate contains dwell and tool changes only.");

        var confidence = unsupported.Count > 0 || noMotion
            ? NcEstimateConfidence.Low
            : result.Errors.Count > 0 ? NcEstimateConfidence.Medium : NcEstimateConfidence.High;
        var warnings = messages
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .Take(MaximumStoredMessages)
            .ToArray();
        return new NcProgramAnalysis(
            NcEngineInfo.AnalysisVersion,
            confidence == NcEstimateConfidence.High ? NcAnalysisStatus.Complete : NcAnalysisStatus.Partial,
            NonNegative(result.FeedSeconds),
            NonNegative(result.RapidDistanceMillimeters),
            Math.Max(0, result.ToolChangeCount),
            NonNegative(result.DwellSeconds),
            string.Equals(result.Units, "inch", StringComparison.OrdinalIgnoreCase) ? "INCH" : "MILLIMETER",
            warnings,
            unsupported,
            confidence,
            analyzedAt);
    }

    /// <summary>A dialect is passed to the engine only when every supporting Machine agrees.</summary>
    internal static string? SingleDialect(IReadOnlyCollection<MachineNcInterpretation> machines)
    {
        var distinct = machines
            .Select(machine => machine.NcDialect)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(NcDialects.Normalize)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return distinct.Length == 1 ? distinct[0] : null;
    }

    /// <summary>
    /// The NC viewer machine passed to the engine: the one every supporting Machine is configured
    /// with. Machines without a configured viewer machine (automatic detection) do not disagree
    /// with the others; when the configured ones differ, the engine detects the machine.
    /// </summary>
    internal static string? SingleViewerMachine(IReadOnlyCollection<MachineNcInterpretation> machines)
    {
        var distinct = machines
            .Select(machine => machine.NcViewerMachine?.Trim())
            .Where(value => !string.IsNullOrEmpty(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return distinct.Length == 1 ? distinct[0] : null;
    }

    /// <summary>
    /// A legacy V1 verification block calls the protected verify subprogram, which is not part of
    /// the release; like the former parser, the engine skips it (line numbers are kept).
    /// </summary>
    internal static string WithoutLegacyHookBlocks(string text)
    {
        if (!text.Contains("MEIMAD", StringComparison.OrdinalIgnoreCase)) return text;
        var lines = text.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            if (NcVerificationHookParser.IsAcceptedHookBlock(lines[index]))
                lines[index] = "(MEIMAD VERIFICATION HOOK)";
        }
        return string.Join('\n', lines);
    }

    private NcEngineRuntime Runtime()
    {
        if (runtime is { IsFaulted: false }) return runtime;
        runtime?.Dispose();
        runtime = runtimeFactory();
        return runtime;
    }

    private void DisposeFaultedRuntime()
    {
        if (runtime is not { IsFaulted: true }) return;
        runtime.Dispose();
        runtime = null;
    }

    private static double NonNegative(double value) => double.IsFinite(value) && value > 0 ? value : 0;

    private static string Trim(string value) => value.Length <= 200 ? value : value[..200] + "…";
}
