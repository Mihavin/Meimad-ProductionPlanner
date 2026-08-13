namespace Meimad.Planner.Server.Domain.CaseOperations;

internal static class CaseOperationValidator
{
    private const int TextMaximum = 200;

    internal static ValidatedCaseOperationCreateValues ValidateAndNormalize(
        CaseOperationCreateValues values)
    {
        var issues = new List<CaseOperationValidationIssue>();
        var caseId = RequiredText(values.CaseId, "caseId", issues);
        var name = RequiredText(values.Name, "name", issues);
        var requiredMachineType = OptionalText(
            values.RequiredMachineType,
            "requiredMachineType",
            issues);
        var predecessorId = OptionalText(
            values.PredecessorCaseOperationId,
            "predecessorCaseOperationId",
            issues);
        var groupKey = OptionalText(
            values.SimultaneousGroupKey,
            "simultaneousGroupKey",
            issues);

        if (values.OperationNumber <= 0)
        {
            issues.Add(new CaseOperationValidationIssue(
                "operationNumber",
                "positive_required",
                "operationNumber must be greater than zero."));
        }

        ValidateNonNegative(values.SetupTimeSeconds, "setupTimeSeconds", issues);
        ValidateNonNegative(
            values.CycleTimePerPartSeconds,
            "cycleTimePerPartSeconds",
            issues);
        ValidateNonNegative(values.QaTimeAfterSetupSeconds, "qaTimeAfterSetupSeconds", issues);
        ValidateNonNegative(values.LoadUnloadTimeSeconds, "loadUnloadTimeSeconds", issues);
        if (values.LoadUnloadEveryNParts <= 0)
        {
            issues.Add(new CaseOperationValidationIssue(
                "loadUnloadEveryNParts", "positive_required",
                "loadUnloadEveryNParts must be greater than zero when supplied."));
        }
        if (!values.AutomaticLoading && values.LoadUnloadEveryNParts.HasValue)
        {
            issues.Add(new CaseOperationValidationIssue(
                "loadUnloadEveryNParts", "automatic_loading_required",
                "loadUnloadEveryNParts is only valid for automatic loading."));
        }
        if (values.AutomaticLoading && values.LoadUnloadRequiresWorker
            && values.LoadUnloadTimeSeconds > 0 && !values.LoadUnloadEveryNParts.HasValue)
        {
            issues.Add(new CaseOperationValidationIssue(
                "loadUnloadEveryNParts", "frequency_required",
                "Automatic loading with worker time requires an every-N-parts frequency."));
        }

        if (!CaseOperationDependencyTypes.TryParseContractToken(
                values.DependencyType?.Trim(),
                out var dependencyType))
        {
            issues.Add(new CaseOperationValidationIssue(
                "dependencyType",
                "invalid_dependency_type",
                "dependencyType must be SEQUENTIAL, PARALLEL_CAPABLE, INDEPENDENT, or LOCKED_SIMULTANEOUS."));
        }
        else
        {
            var hasPredecessor = predecessorId is not null;
            if (dependencyType == CaseOperationDependencyType.Independent && hasPredecessor)
            {
                issues.Add(new CaseOperationValidationIssue(
                    "predecessorCaseOperationId",
                    "predecessor_not_allowed",
                    "INDEPENDENT has no timing or ordering relationship and cannot reference another operation."));
            }
            else if (dependencyType != CaseOperationDependencyType.Independent && !hasPredecessor)
            {
                issues.Add(new CaseOperationValidationIssue(
                    "predecessorCaseOperationId",
                    "predecessor_required",
                    $"{dependencyType.ToContractToken()} requires a referenced Case Operation."));
            }

            if (dependencyType == CaseOperationDependencyType.LockedSimultaneous)
            {
                if (groupKey is null)
                {
                    issues.Add(new CaseOperationValidationIssue(
                        "simultaneousGroupKey",
                        "simultaneous_group_required",
                        "LOCKED_SIMULTANEOUS requires a simultaneous group key."));
                }
            }
            else if (groupKey is not null)
            {
                issues.Add(new CaseOperationValidationIssue(
                    "simultaneousGroupKey",
                    "simultaneous_group_not_allowed",
                    "Only LOCKED_SIMULTANEOUS may declare a simultaneous group key."));
            }
        }

        if (issues.Count > 0)
        {
            throw new CaseOperationValidationException(issues);
        }

        return new ValidatedCaseOperationCreateValues(
            caseId!,
            values.OperationNumber,
            name!,
            requiredMachineType,
            values.SetupTimeSeconds,
            values.CycleTimePerPartSeconds,
            dependencyType,
            predecessorId,
            groupKey,
            values.QaTimeAfterSetupSeconds,
            values.LoadUnloadTimeSeconds,
            values.LoadUnloadRequiresWorker,
            values.AutomaticLoading,
            values.LoadUnloadEveryNParts,
            values.DayShiftOnly);
    }

    private static string? RequiredText(
        string? value,
        string field,
        ICollection<CaseOperationValidationIssue> issues)
    {
        var normalized = OptionalText(value, field, issues);
        if (normalized is null)
        {
            issues.Add(new CaseOperationValidationIssue(field, "required", $"{field} is required."));
        }

        return normalized;
    }

    private static string? OptionalText(
        string? value,
        string field,
        ICollection<CaseOperationValidationIssue> issues)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        if (normalized.Length > TextMaximum)
        {
            issues.Add(new CaseOperationValidationIssue(
                field,
                "too_long",
                $"{field} must contain at most {TextMaximum} characters."));
        }

        return normalized;
    }

    private static void ValidateNonNegative(
        int? value,
        string field,
        ICollection<CaseOperationValidationIssue> issues)
    {
        if (value < 0)
        {
            issues.Add(new CaseOperationValidationIssue(
                field,
                "non_negative_required",
                $"{field} must be zero or greater when supplied."));
        }
    }
}

internal sealed record CaseOperationCreateValues(
    string? CaseId,
    int OperationNumber,
    string? Name,
    string? RequiredMachineType,
    int? SetupTimeSeconds,
    int? CycleTimePerPartSeconds,
    string? DependencyType,
    string? PredecessorCaseOperationId,
    string? SimultaneousGroupKey,
    int QaTimeAfterSetupSeconds = 0,
    int LoadUnloadTimeSeconds = 0,
    bool LoadUnloadRequiresWorker = false,
    bool AutomaticLoading = false,
    int? LoadUnloadEveryNParts = null,
    bool DayShiftOnly = false);

internal sealed record ValidatedCaseOperationCreateValues(
    string CaseId,
    int OperationNumber,
    string Name,
    string? RequiredMachineType,
    int? SetupTimeSeconds,
    int? CycleTimePerPartSeconds,
    CaseOperationDependencyType DependencyType,
    string? PredecessorCaseOperationId,
    string? SimultaneousGroupKey,
    int QaTimeAfterSetupSeconds,
    int LoadUnloadTimeSeconds,
    bool LoadUnloadRequiresWorker,
    bool AutomaticLoading,
    int? LoadUnloadEveryNParts,
    bool DayShiftOnly);

internal sealed record CaseOperationValidationIssue(string Field, string Code, string Message);

internal sealed class CaseOperationValidationException : Exception
{
    internal CaseOperationValidationException(IReadOnlyList<CaseOperationValidationIssue> issues)
        : base("Case Operation validation failed.")
    {
        Issues = issues;
    }

    internal IReadOnlyList<CaseOperationValidationIssue> Issues { get; }
}
