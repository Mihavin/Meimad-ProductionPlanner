namespace Meimad.Planner.Server.Domain.ProductionRuns;

internal sealed class ProductionRunCyclePlanner
{
    internal ProductionRunCyclePlan Calculate(ProductionRunCyclePlanInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.SharedSetupSeconds < 0)
            throw Error("sharedSetupSeconds", "non_negative_required", "Shared setup seconds cannot be negative.");
        if (input.Programs is null || input.Programs.Count == 0)
            throw Error("programs", "required", "A Production Run requires at least one program.");

        var sequencePositions = new HashSet<int>();
        var programIds = new HashSet<string>(StringComparer.Ordinal);
        var calculations = new List<ProgramSeed>(input.Programs.Count);
        foreach (var program in input.Programs.OrderBy(value => value.SequencePosition))
        {
            if (string.IsNullOrWhiteSpace(program.ProgramId) || !programIds.Add(program.ProgramId))
                throw Error("programs.programId", "duplicate_program", "Program IDs must be present and unique.");
            if (program.SequencePosition < 0 || !sequencePositions.Add(program.SequencePosition))
                throw Error("programs.sequencePosition", "duplicate_sequence", "Program sequence positions must be non-negative and unique.");
            if (program.CycleSeconds < 0)
                throw Error("programs.cycleSeconds", "non_negative_required", "Program cycle seconds cannot be negative.");
            if (program.Outputs is null || program.Outputs.Count == 0)
                throw Error("programs.outputs", "required", "A program requires at least one output.");

            var outputIds = new HashSet<string>(StringComparer.Ordinal);
            long? requiredCycles = null;
            var outputs = new List<ProductionRunOutputCyclePlan>(program.Outputs.Count);
            foreach (var output in program.Outputs.OrderBy(value => value.OutputId, StringComparer.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(output.OutputId) || !outputIds.Add(output.OutputId))
                    throw Error("programs.outputs.outputId", "duplicate_output", "A concrete output may appear only once in a program.");
                if (output.QuantityPerCycle <= 0)
                    throw Error("programs.outputs.quantityPerCycle", "positive_required", "Quantity per cycle must be positive.");
                if (output.TargetQuantity <= 0)
                    throw Error("programs.outputs.targetQuantity", "positive_required", "Target quantity must be positive.");
                if (output.RemainingAllocatableQuantity < 0
                    || output.TargetQuantity > output.RemainingAllocatableQuantity)
                    throw Error("programs.outputs.targetQuantity", "over_allocation", "Target quantity exceeds the Batch Operation remaining allocatable quantity.");
                if (output.TargetQuantity % output.QuantityPerCycle != 0)
                    throw Error("programs.outputs.targetQuantity", "not_divisible", "Target quantity must be exactly divisible by quantity per cycle.");

                var cycles = output.TargetQuantity / output.QuantityPerCycle;
                if (requiredCycles.HasValue && requiredCycles.Value != cycles)
                    throw Error("programs.outputs.targetQuantity", "unequal_required_cycles", "All outputs of one program must require exactly the same cycle count.");
                requiredCycles = cycles;
                outputs.Add(new(output.OutputId, output.QuantityPerCycle,
                    output.TargetQuantity, cycles, TimeSpan.Zero));
            }

            var targetCycles = requiredCycles!.Value;
            if (program.CompletedCycleCount < 0 || program.CompletedCycleCount > targetCycles)
                throw Error("programs.completedCycleCount", "cycle_progress_out_of_range", "Completed cycles must be between zero and the exact target.");
            calculations.Add(new(program.ProgramId, program.SequencePosition,
                targetCycles, program.CompletedCycleCount, program.CycleSeconds, outputs));
        }

        var setupTotal = DecimalSeconds(input.SharedSetupSeconds);
        var setupRemaining = input.SetupCompleted ? 0m : setupTotal;
        decimal totalProgramSeconds = 0;
        decimal remainingProgramSeconds = 0;
        checked
        {
            foreach (var value in calculations)
            {
                totalProgramSeconds += value.CycleSeconds * value.TargetCycles;
                remainingProgramSeconds += value.CycleSeconds * (value.TargetCycles - value.CompletedCycles);
            }
        }

        var results = new List<ProductionRunProgramCyclePlan>(calculations.Count);
        foreach (var value in calculations)
        {
            decimal completionFromNow = setupRemaining;
            if (value.CompletedCycles < value.TargetCycles)
            {
                checked
                {
                    foreach (var other in calculations)
                    {
                        var fullRoundsBefore = Math.Max(0,
                            Math.Min(other.TargetCycles, value.TargetCycles - 1) - other.CompletedCycles);
                        completionFromNow += fullRoundsBefore * other.CycleSeconds;
                        if (other.SequencePosition <= value.SequencePosition
                            && other.CompletedCycles < value.TargetCycles
                            && other.TargetCycles >= value.TargetCycles)
                        {
                            completionFromNow += other.CycleSeconds;
                        }
                    }
                }
            }

            var completion = value.CompletedCycles == value.TargetCycles
                ? TimeSpan.Zero : ToTimeSpan(completionFromNow);
            results.Add(new(value.ProgramId, value.SequencePosition, value.TargetCycles,
                value.CompletedCycles, value.TargetCycles - value.CompletedCycles,
                ToTimeSpan(value.CycleSeconds * value.TargetCycles),
                ToTimeSpan(value.CycleSeconds * (value.TargetCycles - value.CompletedCycles)),
                completion,
                value.Outputs.Select(output => output with { ForecastCompletionOffset = completion }).ToArray()));
        }

        return new(ToTimeSpan(setupTotal + totalProgramSeconds),
            ToTimeSpan(setupRemaining + remainingProgramSeconds),
            calculations.Sum(value => value.TargetCycles),
            calculations.Sum(value => value.TargetCycles - value.CompletedCycles),
            results.OrderBy(value => value.SequencePosition).ToArray());
    }

    internal ProductionRunCycleCompletion CompleteOneCycle(
        ProductionRunProgramCyclePlan program)
    {
        if (program.CompletedCycles >= program.TargetCycles)
            throw Error("completedCycleCount", "cycle_target_reached", "A completed program cannot execute an additional cycle.");
        var completed = checked(program.CompletedCycles + 1);
        return new(completed, completed == program.TargetCycles,
            program.Outputs.Select(output => new ProductionRunOutputCycleCompletion(
                output.OutputId, output.QuantityPerCycle,
                checked(output.QuantityPerCycle * completed),
                output.TargetQuantity)).ToArray());
    }

    private static decimal DecimalSeconds(int seconds) => seconds;

    private static TimeSpan ToTimeSpan(decimal seconds)
    {
        if (seconds > (decimal)TimeSpan.MaxValue.TotalSeconds)
            throw Error("duration", "duration_overflow", "Calculated duration exceeds the supported TimeSpan range.");
        return TimeSpan.FromTicks(decimal.ToInt64(decimal.Round(
            seconds * TimeSpan.TicksPerSecond, 0, MidpointRounding.AwayFromZero)));
    }

    private static ProductionRunCycleValidationException Error(
        string field, string code, string message) => new(field, code, message);

    private sealed record ProgramSeed(
        string ProgramId,
        int SequencePosition,
        long TargetCycles,
        long CompletedCycles,
        decimal CycleSeconds,
        IReadOnlyList<ProductionRunOutputCyclePlan> Outputs);
}

internal sealed record ProductionRunCyclePlanInput(
    int SharedSetupSeconds,
    bool SetupCompleted,
    IReadOnlyList<ProductionRunProgramCycleInput> Programs);

internal sealed record ProductionRunProgramCycleInput(
    string ProgramId,
    int SequencePosition,
    decimal CycleSeconds,
    long CompletedCycleCount,
    IReadOnlyList<ProductionRunOutputCycleInput> Outputs);

internal sealed record ProductionRunOutputCycleInput(
    string OutputId,
    long QuantityPerCycle,
    long TargetQuantity,
    long RemainingAllocatableQuantity);

internal sealed record ProductionRunCyclePlan(
    TimeSpan TotalDuration,
    TimeSpan RemainingDuration,
    long TotalProgramExecutions,
    long RemainingProgramExecutions,
    IReadOnlyList<ProductionRunProgramCyclePlan> Programs);

internal sealed record ProductionRunProgramCyclePlan(
    string ProgramId,
    int SequencePosition,
    long TargetCycles,
    long CompletedCycles,
    long RemainingCycles,
    TimeSpan TotalRuntime,
    TimeSpan RemainingRuntime,
    TimeSpan ForecastCompletionOffset,
    IReadOnlyList<ProductionRunOutputCyclePlan> Outputs);

internal sealed record ProductionRunOutputCyclePlan(
    string OutputId,
    long QuantityPerCycle,
    long TargetQuantity,
    long RequiredCycles,
    TimeSpan ForecastCompletionOffset);

internal sealed record ProductionRunCycleCompletion(
    long CompletedCycles,
    bool IsProgramComplete,
    IReadOnlyList<ProductionRunOutputCycleCompletion> Outputs);

internal sealed record ProductionRunOutputCycleCompletion(
    string OutputId,
    long ProducedThisCycle,
    long ProducedTotal,
    long TargetQuantity);

internal sealed class ProductionRunCycleValidationException(
    string field, string code, string message) : Exception(message)
{
    internal string Field { get; } = field;
    internal string Code { get; } = code;
}
