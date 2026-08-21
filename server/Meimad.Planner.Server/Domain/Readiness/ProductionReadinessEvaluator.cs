using Meimad.Planner.Server.Domain.GCode;

namespace Meimad.Planner.Server.Domain.Readiness;

internal static class ProductionReadinessEvaluator
{
    internal static ProductionReadinessResult Evaluate(ProductionReadinessContext context)
    {
        if (context.ActiveProcessRevisionId is null)
            return EvaluateLegacy(context);

        var components = new List<ReadinessComponent>(6);
        var currentReleases = context.ActiveProcessRevisionId is null
            ? []
            : context.Releases
                .Where(release => release.ProcessRevisionId == context.ActiveProcessRevisionId)
                .ToArray();
        var compatible = currentReleases
            .Where(release => context.SupportedPostprocessorIds.Contains(release.PostprocessorId))
            .ToArray();
        var manual = string.Equals(context.ExecutionMode, "MANUAL", StringComparison.Ordinal);
        var effectiveRelease = ResolveGCode(
            context, manual, currentReleases, compatible, components);

        AddToolTable(context, components);
        AddCompatibility(context, manual, currentReleases, compatible, components);
        AddCapacity(context, components);
        AddOffsets(context, effectiveRelease, components);
        AddMaterial(context, components);

        var managed = context.ActiveProcessRevisionId is not null;
        var ready = managed
            ? components.All(component => !component.IsBlocking
                && component.State is ReadinessStates.Ready or ReadinessStates.NotRequired)
            : components.Single(component => component.Key == ReadinessComponentKeys.Material)
                is { IsBlocking: false, State: ReadinessStates.Ready };
        return new ProductionReadinessResult(
            ready ? OverallReadinessStates.ReadyForProduction : OverallReadinessStates.NotReady,
            ready,
            managed,
            components,
            effectiveRelease?.GCodeReleaseId,
            !manual && compatible.Length > 1 && effectiveRelease is null,
            compatible);
    }

    private static ProductionReadinessResult EvaluateLegacy(ProductionReadinessContext context)
    {
        var components = new List<ReadinessComponent>(6)
        {
            Component(ReadinessComponentKeys.GCode, "G-code", ReadinessStates.NotRequired,
                "This legacy Operation has no managed process revision; G-code readiness is not enforced."),
            Component(ReadinessComponentKeys.ToolTable, "Tool Table", ReadinessStates.NotRequired,
                "This legacy Operation has no managed process revision; released tool-table readiness is not enforced."),
            Component(ReadinessComponentKeys.MachinePostprocessorCompatibility,
                "Machine/Postprocessor Compatibility", ReadinessStates.NotRequired,
                "This legacy Operation has no managed process revision; Postprocessor compatibility is not enforced."),
            Component(ReadinessComponentKeys.ToolCapacity, "Tool Capacity", ReadinessStates.NotRequired,
                "This legacy Operation has no released tool requirement count."),
            Component(ReadinessComponentKeys.ToolOffsets, "Tool Offsets", ReadinessStates.NotRequired,
                "This legacy Operation has no managed production context for offset confirmation.")
        };
        AddMaterial(context, components);
        var material = components.Single(component => component.Key == ReadinessComponentKeys.Material);
        var ready = !material.IsBlocking && material.State == ReadinessStates.Ready;
        return new(
            ready ? OverallReadinessStates.ReadyForProduction : OverallReadinessStates.NotReady,
            ready, false, components, null, false, []);
    }

    private static ReadinessRelease? ResolveGCode(
        ProductionReadinessContext context,
        bool manual,
        IReadOnlyList<ReadinessRelease> current,
        IReadOnlyList<ReadinessRelease> compatible,
        ICollection<ReadinessComponent> components)
    {
        if (manual)
        {
            components.Add(Component(ReadinessComponentKeys.GCode, "G-code",
                ReadinessStates.NotRequired,
                "G-code is not required because the assigned Machine execution mode is MANUAL."));
            return null;
        }

        if (context.MachineId is null)
        {
            components.Add(Component(ReadinessComponentKeys.GCode, "G-code",
                ReadinessStates.Unverified,
                "G-code readiness requires a Machine assignment.", true));
            return null;
        }

        if (current.Count == 0)
        {
            var hasHistory = context.Releases.Count > 0;
            components.Add(Component(ReadinessComponentKeys.GCode, "G-code",
                hasHistory ? ReadinessStates.Outdated : ReadinessStates.Missing,
                hasHistory
                    ? "Historical G-code exists, but no release belongs to the active process revision."
                    : "No released G-code exists for the active process revision.", true));
            return null;
        }

        if (compatible.Count == 0)
        {
            components.Add(Component(ReadinessComponentKeys.GCode, "G-code",
                ReadinessStates.Incompatible,
                "Current G-code releases exist, but none uses a Postprocessor supported by the assigned Machine.", true));
            return null;
        }

        if (context.SelectedGCodeReleaseId is not null)
        {
            var selected = context.Releases.FirstOrDefault(
                release => release.GCodeReleaseId == context.SelectedGCodeReleaseId);
            if (selected is null || selected.ProcessRevisionId != context.ActiveProcessRevisionId)
            {
                components.Add(Component(ReadinessComponentKeys.GCode, "G-code",
                    ReadinessStates.Outdated,
                    "The selected G-code release does not belong to the active process revision.", true));
                return null;
            }

            if (!context.SupportedPostprocessorIds.Contains(selected.PostprocessorId))
            {
                components.Add(Component(ReadinessComponentKeys.GCode, "G-code",
                    ReadinessStates.Incompatible,
                    $"The selected {selected.PostprocessorName} release is not supported by the assigned Machine.", true));
                return null;
            }

            components.Add(Component(ReadinessComponentKeys.GCode, "G-code",
                ReadinessStates.Ready,
                $"Selected current release: {selected.PostprocessorName} r{selected.PostSpecificRevision}."));
            return selected;
        }

        if (compatible.Count > 1)
        {
            components.Add(Component(ReadinessComponentKeys.GCode, "G-code",
                ReadinessStates.Blocked,
                $"{compatible.Count} current G-code releases are compatible; select one production release explicitly.", true));
            return null;
        }

        components.Add(Component(ReadinessComponentKeys.GCode, "G-code",
            ReadinessStates.Ready,
            $"One compatible current release is available: {compatible[0].PostprocessorName} r{compatible[0].PostSpecificRevision}."));
        return compatible[0];
    }

    private static void AddToolTable(
        ProductionReadinessContext context,
        ICollection<ReadinessComponent> components)
    {
        if (context.ActiveProcessRevisionId is not null && context.ActiveToolTableReleaseId is not null)
        {
            components.Add(Component(ReadinessComponentKeys.ToolTable, "Tool Table",
                ReadinessStates.Ready,
                "A released tool table belongs to the active process revision."));
            return;
        }

        var hasHistory = context.Releases.Count > 0;
        components.Add(Component(ReadinessComponentKeys.ToolTable, "Tool Table",
            hasHistory ? ReadinessStates.Outdated : ReadinessStates.Missing,
            hasHistory
                ? "Released production history exists, but no tool table belongs to an active process revision."
                : "The Operation has no released tool table for an active process revision.", true));
    }

    private static void AddCompatibility(
        ProductionReadinessContext context,
        bool manual,
        IReadOnlyList<ReadinessRelease> current,
        IReadOnlyList<ReadinessRelease> compatible,
        ICollection<ReadinessComponent> components)
    {
        if (manual)
        {
            components.Add(Component(ReadinessComponentKeys.MachinePostprocessorCompatibility,
                "Machine/Postprocessor Compatibility", ReadinessStates.NotRequired,
                "Postprocessor compatibility is not required for a MANUAL Machine."));
        }
        else if (context.MachineId is null)
        {
            components.Add(Component(ReadinessComponentKeys.MachinePostprocessorCompatibility,
                "Machine/Postprocessor Compatibility", ReadinessStates.Unverified,
                "Compatibility requires a Machine assignment.", true));
        }
        else if (current.Count == 0)
        {
            components.Add(Component(ReadinessComponentKeys.MachinePostprocessorCompatibility,
                "Machine/Postprocessor Compatibility", ReadinessStates.Unverified,
                "Compatibility cannot be verified until current G-code exists.", true));
        }
        else if (compatible.Count == 0)
        {
            components.Add(Component(ReadinessComponentKeys.MachinePostprocessorCompatibility,
                "Machine/Postprocessor Compatibility", ReadinessStates.Incompatible,
                "The assigned Machine supports none of the active process release Postprocessors.", true));
        }
        else
        {
            components.Add(Component(ReadinessComponentKeys.MachinePostprocessorCompatibility,
                "Machine/Postprocessor Compatibility", ReadinessStates.Ready,
                $"The assigned Machine supports {compatible.Count} current release Postprocessor(s)."));
        }
    }

    private static void AddCapacity(
        ProductionReadinessContext context,
        ICollection<ReadinessComponent> components)
    {
        if (context.MachineId is null)
        {
            components.Add(Component(ReadinessComponentKeys.ToolCapacity, "Tool Capacity",
                ReadinessStates.Unverified,
                "Tool capacity requires a Machine assignment.", true));
            return;
        }

        var result = ToolCapacityEvaluator.Evaluate(
            context.RequiredToolCount, context.UsableToolPositions);
        components.Add(Component(ReadinessComponentKeys.ToolCapacity, "Tool Capacity",
            result.IsSatisfied ? ReadinessStates.Ready
                : result.Code == "tool_capacity_mismatch" ? ReadinessStates.Blocked
                : ReadinessStates.Unverified,
            result.Message,
            !result.IsSatisfied));
    }

    private static void AddOffsets(
        ProductionReadinessContext context,
        ReadinessRelease? effectiveRelease,
        ICollection<ReadinessComponent> components)
    {
        if (context.RequiredToolCount == 0)
        {
            components.Add(Component(ReadinessComponentKeys.ToolOffsets, "Tool Offsets",
                ReadinessStates.NotRequired,
                "Tool offsets are not required because the released process uses no magazine tools."));
            return;
        }

        if (context.MachineId is null || context.ActiveProcessRevisionId is null)
        {
            components.Add(Component(ReadinessComponentKeys.ToolOffsets, "Tool Offsets",
                ReadinessStates.Unverified,
                "Tool offsets require an assigned Machine and active process revision.", true));
            return;
        }

        var expectedGCodeId = string.Equals(context.ExecutionMode, "MANUAL", StringComparison.Ordinal)
            ? null
            : effectiveRelease?.GCodeReleaseId ?? context.SelectedGCodeReleaseId;
        if (!string.Equals(context.ExecutionMode, "MANUAL", StringComparison.Ordinal)
            && expectedGCodeId is null)
        {
            components.Add(Component(ReadinessComponentKeys.ToolOffsets, "Tool Offsets",
                ReadinessStates.Blocked,
                "Tool offsets cannot be confirmed until one current compatible G-code release is selected.", true));
            return;
        }

        var exact = context.ToolOffsetFacts
            .Where(fact => fact.MachineId == context.MachineId
                && fact.ProcessRevisionId == context.ActiveProcessRevisionId
                && fact.GCodeReleaseId == expectedGCodeId)
            .OrderByDescending(fact => fact.RecordedAt)
            .FirstOrDefault();
        if (exact is null)
        {
            var outdated = context.ToolOffsetFacts.Count > 0;
            components.Add(Component(ReadinessComponentKeys.ToolOffsets, "Tool Offsets",
                outdated ? ReadinessStates.Outdated : ReadinessStates.Missing,
                outdated
                    ? "Tool-offset confirmation exists only for an older Machine or production configuration."
                    : "Tool offsets have not been confirmed for this Machine and production configuration.", true));
            return;
        }

        var state = exact.Status switch
        {
            ReadinessStates.Ready => ReadinessStates.Ready,
            ReadinessStates.Missing => ReadinessStates.Missing,
            _ => ReadinessStates.Unverified
        };
        components.Add(Component(ReadinessComponentKeys.ToolOffsets, "Tool Offsets", state,
            exact.Comment ?? (state == ReadinessStates.Ready
                ? "Tool offsets are confirmed for this production configuration."
                : "Tool offsets are not ready for this production configuration."),
            state != ReadinessStates.Ready));
    }

    private static void AddMaterial(
        ProductionReadinessContext context,
        ICollection<ReadinessComponent> components)
    {
        var state = context.MaterialStatus switch
        {
            ReadinessStates.Ready => ReadinessStates.Ready,
            ReadinessStates.Missing => ReadinessStates.Missing,
            _ => ReadinessStates.Unverified
        };
        components.Add(Component(ReadinessComponentKeys.Material, "Material", state,
            context.MaterialComment ?? (state switch
            {
                ReadinessStates.Ready => "Material availability was physically confirmed for this Batch Operation.",
                ReadinessStates.Missing => "Required material is reported missing.",
                _ => "Material has not been physically verified; historical Kitaron receipts are not used."
            }),
            state != ReadinessStates.Ready));
    }

    private static ReadinessComponent Component(
        string key,
        string label,
        string state,
        string message,
        bool blocking = false) => new(key, label, state, message, blocking);
}
