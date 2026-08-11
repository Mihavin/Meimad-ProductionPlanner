using System.Collections.ObjectModel;

namespace Meimad.Planner.Server.Domain.CaseOperations;

internal sealed class CaseOperationGraph
{
    private readonly IReadOnlyDictionary<string, CaseOperation> operations;
    private readonly IReadOnlyList<CaseOperationDependency> dependencies;
    private readonly IReadOnlyDictionary<string, IReadOnlySet<string>> lockedGroups;

    private CaseOperationGraph(
        IReadOnlyDictionary<string, CaseOperation> operations,
        IReadOnlyList<CaseOperationDependency> dependencies,
        IReadOnlyDictionary<string, IReadOnlySet<string>> lockedGroups)
    {
        this.operations = operations;
        this.dependencies = dependencies;
        this.lockedGroups = lockedGroups;
    }

    internal IReadOnlyDictionary<string, CaseOperation> Operations => operations;

    internal IReadOnlyList<CaseOperationDependency> Dependencies => dependencies;

    internal static CaseOperationGraph Create(
        string caseId,
        IEnumerable<CaseOperation> operations,
        IEnumerable<CaseOperationDependency> dependencies)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(dependencies);

        var issues = new List<CaseOperationGraphIssue>();
        if (string.IsNullOrWhiteSpace(caseId))
        {
            issues.Add(new CaseOperationGraphIssue(
                "caseId",
                "required",
                "caseId is required."));
        }

        var operationArray = operations.ToArray();
        var dependencyArray = dependencies.ToArray();
        var operationMap = ValidateOperations(caseId, operationArray, issues);
        var lockedGroupMembers = ValidateDependencies(
            operationMap,
            dependencyArray,
            issues);

        ValidateSequentialGraph(
            operationMap.Keys,
            dependencyArray,
            lockedGroupMembers,
            issues);

        if (issues.Count > 0)
        {
            throw new CaseOperationGraphValidationException(issues);
        }

        var readOnlyOperations = new ReadOnlyDictionary<string, CaseOperation>(operationMap);
        var readOnlyGroups = new ReadOnlyDictionary<string, IReadOnlySet<string>>(
            lockedGroupMembers.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlySet<string>)new ReadOnlySet<string>(pair.Value),
                StringComparer.Ordinal));

        return new CaseOperationGraph(
            readOnlyOperations,
            Array.AsReadOnly(dependencyArray),
            readOnlyGroups);
    }

    internal IReadOnlyList<string> GetSequentialPrerequisiteIds(string dependentOperationId)
    {
        return dependencies
            .Where(dependency =>
                dependency.Type == CaseOperationDependencyType.Sequential
                && string.Equals(
                    dependency.ToCaseOperationId,
                    dependentOperationId,
                    StringComparison.Ordinal))
            .Select(dependency => dependency.FromCaseOperationId)
            .ToArray();
    }

    internal IReadOnlySet<string> GetLockedSimultaneousGroupMembers(string groupKey)
    {
        return lockedGroups.TryGetValue(groupKey, out var members)
            ? members
            : ReadOnlySet<string>.Empty;
    }

    private static Dictionary<string, CaseOperation> ValidateOperations(
        string caseId,
        IReadOnlyList<CaseOperation> operations,
        ICollection<CaseOperationGraphIssue> issues)
    {
        var result = new Dictionary<string, CaseOperation>(StringComparer.Ordinal);
        var operationNumbers = new Dictionary<int, string>();
        var routePositions = new Dictionary<int, string>();

        for (var index = 0; index < operations.Count; index++)
        {
            var operation = operations[index];
            var field = $"operations[{index}]";
            if (string.IsNullOrWhiteSpace(operation.CaseOperationId))
            {
                AddIssue(issues, field, "operation_id_required", "Operation ID is required.");
            }
            else if (!result.TryAdd(operation.CaseOperationId, operation))
            {
                AddIssue(
                    issues,
                    field,
                    "duplicate_operation_id",
                    $"Operation ID '{operation.CaseOperationId}' occurs more than once.");
            }

            if (!string.Equals(operation.CaseId, caseId, StringComparison.Ordinal))
            {
                AddIssue(
                    issues,
                    field,
                    "case_mismatch",
                    "Every Case Operation in the graph must belong to the graph Case.");
            }

            if (operation.OperationNumber <= 0)
            {
                AddIssue(
                    issues,
                    $"{field}.operationNumber",
                    "positive_required",
                    "Operation number must be greater than zero.");
            }
            else if (!operationNumbers.TryAdd(
                operation.OperationNumber,
                operation.CaseOperationId))
            {
                AddIssue(
                    issues,
                    $"{field}.operationNumber",
                    "duplicate_operation_number",
                    "Operation number must be unique within a Case route.");
            }

            if (operation.RoutePosition < 0)
            {
                AddIssue(
                    issues,
                    $"{field}.routePosition",
                    "non_negative_required",
                    "Route position must be zero or greater.");
            }
            else if (!routePositions.TryAdd(
                operation.RoutePosition,
                operation.CaseOperationId))
            {
                AddIssue(
                    issues,
                    $"{field}.routePosition",
                    "duplicate_route_position",
                    "Route position must be unique within a Case route.");
            }

            ValidateText(operation.Name, $"{field}.name", required: true, issues);
            ValidateText(
                operation.RequiredMachineType,
                $"{field}.requiredMachineType",
                required: false,
                issues);
            ValidateNonNegative(
                operation.SetupTimeSeconds,
                $"{field}.setupTimeSeconds",
                issues);
            ValidateNonNegative(
                operation.CycleTimePerPartSeconds,
                $"{field}.cycleTimePerPartSeconds",
                issues);

            if (operation.Version <= 0)
            {
                AddIssue(
                    issues,
                    $"{field}.version",
                    "positive_required",
                    "Version must be greater than zero.");
            }

            if (operation.UpdatedAt < operation.CreatedAt)
            {
                AddIssue(
                    issues,
                    $"{field}.updatedAt",
                    "timestamp_order_invalid",
                    "Updated timestamp cannot precede Created timestamp.");
            }
        }

        return result;
    }

    private static Dictionary<string, HashSet<string>> ValidateDependencies(
        IReadOnlyDictionary<string, CaseOperation> operations,
        IReadOnlyList<CaseOperationDependency> dependencies,
        ICollection<CaseOperationGraphIssue> issues)
    {
        var dependencyIds = new HashSet<string>(StringComparer.Ordinal);
        var directRelationships = new Dictionary<(string First, string Second), Relationship>();
        var lockedGroupByOperation = new Dictionary<string, string>(StringComparer.Ordinal);
        var lockedGroups = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        for (var index = 0; index < dependencies.Count; index++)
        {
            var dependency = dependencies[index];
            var field = $"dependencies[{index}]";
            var valid = true;

            if (string.IsNullOrWhiteSpace(dependency.DependencyId))
            {
                AddIssue(issues, field, "dependency_id_required", "Dependency ID is required.");
                valid = false;
            }
            else if (!dependencyIds.Add(dependency.DependencyId))
            {
                AddIssue(
                    issues,
                    field,
                    "duplicate_dependency_id",
                    $"Dependency ID '{dependency.DependencyId}' occurs more than once.");
                valid = false;
            }

            if (!Enum.IsDefined(typeof(CaseOperationDependencyType), dependency.Type))
            {
                AddIssue(issues, field, "dependency_type_invalid", "Dependency type is invalid.");
                valid = false;
            }

            valid &= ValidateReference(
                operations,
                dependency.FromCaseOperationId,
                $"{field}.fromCaseOperationId",
                issues);
            valid &= ValidateReference(
                operations,
                dependency.ToCaseOperationId,
                $"{field}.toCaseOperationId",
                issues);

            if (string.Equals(
                dependency.FromCaseOperationId,
                dependency.ToCaseOperationId,
                StringComparison.Ordinal))
            {
                AddIssue(
                    issues,
                    field,
                    "self_reference",
                    "A Case Operation cannot depend on or be linked to itself.");
                valid = false;
            }

            var groupKey = dependency.SimultaneousGroupKey?.Trim();
            if (dependency.Type == CaseOperationDependencyType.LockedSimultaneous)
            {
                if (string.IsNullOrEmpty(groupKey))
                {
                    AddIssue(
                        issues,
                        $"{field}.simultaneousGroupKey",
                        "simultaneous_group_required",
                        "LOCKED_SIMULTANEOUS requires a group key.");
                    valid = false;
                }
            }
            else if (!string.IsNullOrEmpty(groupKey))
            {
                AddIssue(
                    issues,
                    $"{field}.simultaneousGroupKey",
                    "simultaneous_group_not_allowed",
                    "Only LOCKED_SIMULTANEOUS may declare a simultaneous group.");
                valid = false;
            }

            if (!valid)
            {
                continue;
            }

            ValidateDirectRelationship(dependency, directRelationships, issues, field);

            if (dependency.Type == CaseOperationDependencyType.LockedSimultaneous)
            {
                AddLockedMember(
                    dependency.FromCaseOperationId,
                    groupKey!,
                    lockedGroupByOperation,
                    lockedGroups,
                    issues,
                    field);
                AddLockedMember(
                    dependency.ToCaseOperationId,
                    groupKey!,
                    lockedGroupByOperation,
                    lockedGroups,
                    issues,
                    field);
            }
        }

        return lockedGroups;
    }

    private static void ValidateSequentialGraph(
        IEnumerable<string> operationIds,
        IReadOnlyList<CaseOperationDependency> dependencies,
        IReadOnlyDictionary<string, HashSet<string>> lockedGroups,
        ICollection<CaseOperationGraphIssue> issues)
    {
        var unionFind = new UnionFind(operationIds);
        foreach (var group in lockedGroups.Values)
        {
            var first = group.FirstOrDefault();
            if (first is null)
            {
                continue;
            }

            foreach (var member in group.Skip(1))
            {
                unionFind.Union(first, member);
            }
        }

        var roots = operationIds
            .Select(unionFind.Find)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var outgoing = roots.ToDictionary(
            root => root,
            _ => new HashSet<string>(StringComparer.Ordinal),
            StringComparer.Ordinal);
        var indegree = roots.ToDictionary(root => root, _ => 0, StringComparer.Ordinal);

        foreach (var dependency in dependencies.Where(dependency =>
                     dependency.Type == CaseOperationDependencyType.Sequential))
        {
            if (!unionFind.Contains(dependency.FromCaseOperationId)
                || !unionFind.Contains(dependency.ToCaseOperationId)
                || string.Equals(
                    dependency.FromCaseOperationId,
                    dependency.ToCaseOperationId,
                    StringComparison.Ordinal))
            {
                continue;
            }

            var fromRoot = unionFind.Find(dependency.FromCaseOperationId);
            var toRoot = unionFind.Find(dependency.ToCaseOperationId);
            if (string.Equals(fromRoot, toRoot, StringComparison.Ordinal))
            {
                AddIssue(
                    issues,
                    $"dependencies[{dependency.DependencyId}]",
                    "locked_group_ordering_conflict",
                    "A sequential dependency cannot order members of one locked-simultaneous group.");
                continue;
            }

            if (outgoing[fromRoot].Add(toRoot))
            {
                indegree[toRoot]++;
            }
        }

        var ready = new Queue<string>(indegree
            .Where(pair => pair.Value == 0)
            .Select(pair => pair.Key));
        var visited = 0;
        while (ready.Count > 0)
        {
            var current = ready.Dequeue();
            visited++;
            foreach (var dependent in outgoing[current])
            {
                indegree[dependent]--;
                if (indegree[dependent] == 0)
                {
                    ready.Enqueue(dependent);
                }
            }
        }

        if (visited != roots.Length)
        {
            AddIssue(
                issues,
                "dependencies",
                "sequential_cycle",
                "Sequential dependencies must form an acyclic graph after locked groups are collapsed.");
        }
    }

    private static bool ValidateReference(
        IReadOnlyDictionary<string, CaseOperation> operations,
        string operationId,
        string field,
        ICollection<CaseOperationGraphIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(operationId) || !operations.ContainsKey(operationId))
        {
            AddIssue(
                issues,
                field,
                "invalid_reference",
                $"Referenced Case Operation '{operationId}' does not exist in this Case graph.");
            return false;
        }

        return true;
    }

    private static void ValidateDirectRelationship(
        CaseOperationDependency dependency,
        IDictionary<(string First, string Second), Relationship> relationships,
        ICollection<CaseOperationGraphIssue> issues,
        string field)
    {
        var pair = MakePair(dependency.FromCaseOperationId, dependency.ToCaseOperationId);
        if (!relationships.TryGetValue(pair, out var existing))
        {
            relationships.Add(pair, new Relationship(
                dependency.Type,
                dependency.FromCaseOperationId,
                dependency.ToCaseOperationId));
            return;
        }

        if (existing.Type != dependency.Type)
        {
            AddIssue(
                issues,
                field,
                "conflicting_relationship",
                "One operation pair cannot have multiple dependency meanings.");
            return;
        }

        var sameDirectedSequential = dependency.Type == CaseOperationDependencyType.Sequential
            && string.Equals(existing.From, dependency.FromCaseOperationId, StringComparison.Ordinal)
            && string.Equals(existing.To, dependency.ToCaseOperationId, StringComparison.Ordinal);
        if (dependency.Type != CaseOperationDependencyType.Sequential || sameDirectedSequential)
        {
            AddIssue(
                issues,
                field,
                "duplicate_relationship",
                "The dependency relationship is duplicated.");
        }
    }

    private static void AddLockedMember(
        string operationId,
        string groupKey,
        IDictionary<string, string> groupByOperation,
        IDictionary<string, HashSet<string>> groups,
        ICollection<CaseOperationGraphIssue> issues,
        string field)
    {
        if (groupByOperation.TryGetValue(operationId, out var existingGroup)
            && !string.Equals(existingGroup, groupKey, StringComparison.Ordinal))
        {
            AddIssue(
                issues,
                field,
                "multiple_simultaneous_groups",
                $"Operation '{operationId}' cannot belong to more than one locked-simultaneous group.");
            return;
        }

        groupByOperation[operationId] = groupKey;
        if (!groups.TryGetValue(groupKey, out var members))
        {
            members = new HashSet<string>(StringComparer.Ordinal);
            groups.Add(groupKey, members);
        }

        members.Add(operationId);
    }

    private static void ValidateText(
        string? value,
        string field,
        bool required,
        ICollection<CaseOperationGraphIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            if (required)
            {
                AddIssue(issues, field, "required", $"{field} is required.");
            }

            return;
        }

        if (value.Trim().Length > 200)
        {
            AddIssue(issues, field, "too_long", $"{field} must contain at most 200 characters.");
        }
    }

    private static void ValidateNonNegative(
        int? value,
        string field,
        ICollection<CaseOperationGraphIssue> issues)
    {
        if (value < 0)
        {
            AddIssue(
                issues,
                field,
                "non_negative_required",
                $"{field} must be zero or greater when supplied.");
        }
    }

    private static (string First, string Second) MakePair(string first, string second) =>
        string.CompareOrdinal(first, second) <= 0
            ? (first, second)
            : (second, first);

    private static void AddIssue(
        ICollection<CaseOperationGraphIssue> issues,
        string field,
        string code,
        string message)
    {
        issues.Add(new CaseOperationGraphIssue(field, code, message));
    }

    private sealed record Relationship(
        CaseOperationDependencyType Type,
        string From,
        string To);

    private sealed class UnionFind
    {
        private readonly Dictionary<string, string> parents;

        internal UnionFind(IEnumerable<string> items)
        {
            parents = items.Distinct(StringComparer.Ordinal).ToDictionary(
                item => item,
                item => item,
                StringComparer.Ordinal);
        }

        internal bool Contains(string item) => parents.ContainsKey(item);

        internal string Find(string item)
        {
            var parent = parents[item];
            if (!string.Equals(parent, item, StringComparison.Ordinal))
            {
                parents[item] = Find(parent);
            }

            return parents[item];
        }

        internal void Union(string first, string second)
        {
            var firstRoot = Find(first);
            var secondRoot = Find(second);
            if (!string.Equals(firstRoot, secondRoot, StringComparison.Ordinal))
            {
                parents[secondRoot] = firstRoot;
            }
        }
    }

    private sealed class ReadOnlySet<T> : IReadOnlySet<T>
        where T : notnull
    {
        private readonly HashSet<T> values;

        internal ReadOnlySet(IEnumerable<T> values)
        {
            this.values = new HashSet<T>(values);
        }

        private ReadOnlySet()
        {
            values = [];
        }

        internal static ReadOnlySet<T> Empty { get; } = new();

        public int Count => values.Count;

        public bool Contains(T item) => values.Contains(item);

        public bool IsProperSubsetOf(IEnumerable<T> other) => values.IsProperSubsetOf(other);

        public bool IsProperSupersetOf(IEnumerable<T> other) => values.IsProperSupersetOf(other);

        public bool IsSubsetOf(IEnumerable<T> other) => values.IsSubsetOf(other);

        public bool IsSupersetOf(IEnumerable<T> other) => values.IsSupersetOf(other);

        public bool Overlaps(IEnumerable<T> other) => values.Overlaps(other);

        public bool SetEquals(IEnumerable<T> other) => values.SetEquals(other);

        public IEnumerator<T> GetEnumerator() => values.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }
}

internal sealed record CaseOperationGraphIssue(string Field, string Code, string Message);

internal sealed class CaseOperationGraphValidationException : Exception
{
    internal CaseOperationGraphValidationException(IReadOnlyList<CaseOperationGraphIssue> issues)
        : base("Case Operation graph validation failed.")
    {
        Issues = issues;
    }

    internal IReadOnlyList<CaseOperationGraphIssue> Issues { get; }
}
