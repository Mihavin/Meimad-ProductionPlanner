using System.Text.Json;
using System.Text.Json.Serialization;
using Meimad.Planner.Server.Application.Cases;
using Meimad.Planner.Server.Domain.Cases;

namespace Meimad.Planner.Server.Api.Cases;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CreateCaseRequest(
    string? PartNumber,
    string? Name,
    string? Revision,
    string? Customer,
    string? CustomerReference,
    string? PreviewPath,
    string? WorkingFolderPath,
    string? MaterialType,
    string? MaterialSpecification,
    string? RawMaterialForm,
    string? RawMaterialDimensions,
    string? Notes)
{
    internal CreateCaseCommand ToCommand() => new(
        PartNumber,
        Name,
        Revision,
        Customer,
        CustomerReference,
        PreviewPath,
        WorkingFolderPath,
        MaterialType,
        MaterialSpecification,
        RawMaterialForm,
        RawMaterialDimensions,
        Notes);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CreateCaseOperationRequest(
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
    bool DayShiftOnly = false,
    bool HasExternalDelay = false,
    string? ExternalDelayDescription = null,
    double ExternalDelayDuration = 0,
    string ExternalDelayDurationUnit = "hours",
    string? ExternalDelayCalendarId = null,
    bool RespectMasterCalendar = true)
{
    internal CreateCaseOperationCommand ToCommand() => new(
        OperationNumber,
        Name,
        RequiredMachineType,
        SetupTimeSeconds,
        CycleTimePerPartSeconds,
        DependencyType,
        PredecessorCaseOperationId,
        SimultaneousGroupKey,
        QaTimeAfterSetupSeconds,
        LoadUnloadTimeSeconds,
        LoadUnloadRequiresWorker,
        AutomaticLoading,
        LoadUnloadEveryNParts,
        DayShiftOnly,
        HasExternalDelay,
        ExternalDelayDescription,
        ExternalDelayDuration,
        ExternalDelayDurationUnit,
        ExternalDelayCalendarId,
        RespectMasterCalendar);
}

internal sealed class PatchCaseRequest
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Fields { get; init; } =
        new(StringComparer.Ordinal);

    internal UpdateCaseCommand ToCommand()
    {
        var reader = new PatchFieldReader(Fields);
        var command = new UpdateCaseCommand(
            reader.ReadString("partNumber"),
            reader.ReadString("name"),
            reader.ReadString("revision"),
            reader.ReadString("customer"),
            reader.ReadString("customerReference"),
            reader.ReadString("previewPath"),
            reader.ReadString("workingFolderPath"),
            reader.ReadString("materialType"),
            reader.ReadString("materialSpecification"),
            reader.ReadString("rawMaterialForm"),
            reader.ReadString("rawMaterialDimensions"),
            reader.ReadString("notes"));

        reader.ThrowIfInvalid();
        return command;
    }

    private sealed class PatchFieldReader
    {
        private static readonly HashSet<string> AllowedFields =
        [
            "partNumber",
            "name",
            "revision",
            "customer",
            "customerReference",
            "previewPath",
            "workingFolderPath",
            "materialType",
            "materialSpecification",
            "rawMaterialForm",
            "rawMaterialDimensions",
            "notes"
        ];

        private readonly IReadOnlyDictionary<string, JsonElement> fields;
        private readonly List<CaseRequestIssue> issues = [];

        internal PatchFieldReader(IReadOnlyDictionary<string, JsonElement> fields)
        {
            this.fields = fields;
            foreach (var field in fields.Keys)
            {
                if (!AllowedFields.Contains(field))
                {
                    issues.Add(new CaseRequestIssue(
                        field,
                        "unknown_field",
                        $"Field '{field}' is not supported."));
                }
            }

            if (fields.Count == 0)
            {
                issues.Add(new CaseRequestIssue(
                    string.Empty,
                    "empty_patch",
                    "At least one Case field must be supplied."));
            }
        }

        internal OptionalField<string?> ReadString(string name)
        {
            if (!fields.TryGetValue(name, out var element))
            {
                return OptionalField<string?>.Unspecified;
            }

            if (element.ValueKind == JsonValueKind.Null)
            {
                return OptionalField<string?>.Specified(null);
            }

            if (element.ValueKind == JsonValueKind.String)
            {
                return OptionalField<string?>.Specified(element.GetString());
            }

            AddTypeIssue(name, "string or null");
            return OptionalField<string?>.Unspecified;
        }

        internal void ThrowIfInvalid()
        {
            if (issues.Count > 0)
            {
                throw new CaseRequestException(issues);
            }
        }

        private void AddTypeIssue(string name, string expected)
        {
            issues.Add(new CaseRequestIssue(
                name,
                "invalid_type",
                $"Field '{name}' must be a {expected}."));
        }
    }
}

internal sealed class PatchCaseOperationRequest
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Fields { get; init; } =
        new(StringComparer.Ordinal);

    internal UpdateCaseOperationCommand ToCommand()
    {
        var reader = new PatchFieldReader(Fields);
        var command = new UpdateCaseOperationCommand(
            reader.ReadInt32("operationNumber"),
            reader.ReadString("name"),
            reader.ReadString("requiredMachineType"),
            reader.ReadNullableInt32("setupTimeSeconds"),
            reader.ReadNullableInt32("cycleTimePerPartSeconds"),
            reader.ReadString("dependencyType"),
            reader.ReadString("predecessorCaseOperationId"),
            reader.ReadString("simultaneousGroupKey"),
            reader.ReadInt32("qaTimeAfterSetupSeconds"),
            reader.ReadInt32("loadUnloadTimeSeconds"),
            reader.ReadBoolean("loadUnloadRequiresWorker"),
            reader.ReadBoolean("automaticLoading"),
            reader.ReadNullableInt32("loadUnloadEveryNParts"),
            reader.ReadBoolean("dayShiftOnly"),
            reader.ReadBoolean("hasExternalDelay"),
            reader.ReadString("externalDelayDescription"),
            reader.ReadDouble("externalDelayDuration"),
            reader.ReadString("externalDelayDurationUnit"),
            reader.ReadString("externalDelayCalendarId"),
            reader.ReadBoolean("respectMasterCalendar"));

        reader.ThrowIfInvalid();
        return command;
    }

    private sealed class PatchFieldReader
    {
        private static readonly HashSet<string> AllowedFields =
        [
            "operationNumber",
            "name",
            "requiredMachineType",
            "setupTimeSeconds",
            "cycleTimePerPartSeconds",
            "dependencyType",
            "predecessorCaseOperationId",
            "simultaneousGroupKey",
            "qaTimeAfterSetupSeconds",
            "loadUnloadTimeSeconds",
            "loadUnloadRequiresWorker",
            "automaticLoading",
            "loadUnloadEveryNParts",
            "dayShiftOnly",
            "hasExternalDelay",
            "externalDelayDescription",
            "externalDelayDuration",
            "externalDelayDurationUnit",
            "externalDelayCalendarId",
            "respectMasterCalendar"
        ];

        private readonly IReadOnlyDictionary<string, JsonElement> fields;
        private readonly List<CaseRequestIssue> issues = [];

        internal PatchFieldReader(IReadOnlyDictionary<string, JsonElement> fields)
        {
            this.fields = fields;
            foreach (var field in fields.Keys.Where(field => !AllowedFields.Contains(field)))
            {
                issues.Add(new CaseRequestIssue(
                    field,
                    "unknown_field",
                    $"Field '{field}' is not supported."));
            }

            if (fields.Count == 0)
            {
                issues.Add(new CaseRequestIssue(
                    string.Empty,
                    "empty_patch",
                    "At least one Case Operation field must be supplied."));
            }
        }

        internal OptionalField<int> ReadInt32(string name)
        {
            if (!fields.TryGetValue(name, out var element))
            {
                return OptionalField<int>.Unspecified;
            }

            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value))
            {
                return OptionalField<int>.Specified(value);
            }

            AddTypeIssue(name, "32-bit integer");
            return OptionalField<int>.Unspecified;
        }

        internal OptionalField<int?> ReadNullableInt32(string name)
        {
            if (!fields.TryGetValue(name, out var element))
            {
                return OptionalField<int?>.Unspecified;
            }

            if (element.ValueKind == JsonValueKind.Null)
            {
                return OptionalField<int?>.Specified(null);
            }

            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value))
            {
                return OptionalField<int?>.Specified(value);
            }

            AddTypeIssue(name, "32-bit integer or null");
            return OptionalField<int?>.Unspecified;
        }

        internal OptionalField<bool> ReadBoolean(string name)
        {
            if (!fields.TryGetValue(name, out var element))
            {
                return OptionalField<bool>.Unspecified;
            }

            if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return OptionalField<bool>.Specified(element.GetBoolean());
            }

            AddTypeIssue(name, "boolean");
            return OptionalField<bool>.Unspecified;
        }

        internal OptionalField<double> ReadDouble(string name)
        {
            if (!fields.TryGetValue(name, out var element))
            {
                return OptionalField<double>.Unspecified;
            }

            if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var value))
            {
                return OptionalField<double>.Specified(value);
            }

            AddTypeIssue(name, "number");
            return OptionalField<double>.Unspecified;
        }

        internal OptionalField<string?> ReadString(string name)
        {
            if (!fields.TryGetValue(name, out var element))
            {
                return OptionalField<string?>.Unspecified;
            }

            if (element.ValueKind == JsonValueKind.Null)
            {
                return OptionalField<string?>.Specified(null);
            }

            if (element.ValueKind == JsonValueKind.String)
            {
                return OptionalField<string?>.Specified(element.GetString());
            }

            AddTypeIssue(name, "string or null");
            return OptionalField<string?>.Unspecified;
        }

        internal void ThrowIfInvalid()
        {
            if (issues.Count > 0)
            {
                throw new CaseRequestException(issues);
            }
        }

        private void AddTypeIssue(string name, string expected) =>
            issues.Add(new CaseRequestIssue(
                name,
                "invalid_type",
                $"Field '{name}' must be a {expected}."));
    }
}

internal sealed record CaseResponse(
    string CaseId,
    string PartNumber,
    string Name,
    string? Revision,
    string? Customer,
    string? CustomerReference,
    string? PreviewPath,
    string WorkingFolderPath,
    string? MaterialType,
    string? MaterialSpecification,
    string? RawMaterialForm,
    string? RawMaterialDimensions,
    int? CurrentSetupTimeSeconds,
    int? CurrentCycleTimePerPartSeconds,
    string? Notes,
    bool IsActive,
    bool IsParent,
    bool IsChild,
    bool IsKitaronManaged,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    internal static CaseResponse FromDomain(PlannerCase plannerCase) => new(
        plannerCase.CaseId,
        plannerCase.PartNumber,
        plannerCase.Name,
        plannerCase.Revision,
        plannerCase.Customer,
        plannerCase.CustomerReference,
        plannerCase.PreviewPath,
        plannerCase.WorkingFolderPath,
        plannerCase.MaterialType,
        plannerCase.MaterialSpecification,
        plannerCase.RawMaterialForm,
        plannerCase.RawMaterialDimensions,
        plannerCase.CurrentSetupTimeSeconds,
        plannerCase.CurrentCycleTimePerPartSeconds,
        plannerCase.Notes,
        plannerCase.IsActive,
        plannerCase.IsParent,
        plannerCase.IsChild,
        plannerCase.IsKitaronManaged,
        plannerCase.Version,
        plannerCase.CreatedAt,
        plannerCase.UpdatedAt);
}

internal sealed record CaseListResponse(IReadOnlyList<CaseResponse> Items, string? NextCursor);

internal sealed record CaseOperationResponse(
    string CaseOperationId,
    string CaseId,
    int OperationNumber,
    int RoutePosition,
    string Name,
    string? RequiredMachineType,
    int? SetupTimeSeconds,
    int? CycleTimePerPartSeconds,
    string DependencyType,
    string? PredecessorCaseOperationId,
    string? SimultaneousGroupKey,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int QaTimeAfterSetupSeconds,
    int LoadUnloadTimeSeconds,
    bool LoadUnloadRequiresWorker,
    bool AutomaticLoading,
    int? LoadUnloadEveryNParts,
    bool DayShiftOnly,
    bool HasExternalDelay,
    string? ExternalDelayDescription,
    double ExternalDelayDuration,
    string ExternalDelayDurationUnit,
    string? ExternalDelayCalendarId,
    bool RespectMasterCalendar)
{
    internal static CaseOperationResponse FromApplication(CaseOperationDetails operation) => new(
        operation.CaseOperationId,
        operation.CaseId,
        operation.OperationNumber,
        operation.RoutePosition,
        operation.Name,
        operation.RequiredMachineType,
        operation.SetupTimeSeconds,
        operation.CycleTimePerPartSeconds,
        operation.DependencyType,
        operation.PredecessorCaseOperationId,
        operation.SimultaneousGroupKey,
        operation.Version,
        operation.CreatedAt,
        operation.UpdatedAt,
        operation.QaTimeAfterSetupSeconds,
        operation.LoadUnloadTimeSeconds,
        operation.LoadUnloadRequiresWorker,
        operation.AutomaticLoading,
        operation.LoadUnloadEveryNParts,
        operation.DayShiftOnly,
        operation.HasExternalDelay,
        operation.ExternalDelayDescription,
        operation.ExternalDelayDuration,
        operation.ExternalDelayDurationUnit,
        operation.ExternalDelayCalendarId,
        operation.RespectMasterCalendar);
}

internal sealed record CaseOperationListResponse(
    IReadOnlyList<CaseOperationResponse> Items,
    string? NextCursor);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CreateCaseComponentRequest(
    string? ChildCaseId,
    double QuantityPerParent,
    int SortOrder = 0,
    string? Notes = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record UpdateCaseComponentRequest(
    double QuantityPerParent,
    int SortOrder,
    string? Notes,
    bool IsActive);

internal sealed record CaseComponentResponse(
    string CaseComponentId,
    string ParentCaseId,
    string ParentPartNumber,
    string ParentCaseName,
    string ChildCaseId,
    string ChildPartNumber,
    string ChildCaseName,
    double QuantityPerParent,
    int SortOrder,
    string? Notes,
    bool IsActive,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    internal static CaseComponentResponse FromApplication(CaseComponentDetails item) => new(
        item.CaseComponentId, item.ParentCaseId, item.ParentPartNumber, item.ParentCaseName,
        item.ChildCaseId, item.ChildPartNumber, item.ChildCaseName, item.QuantityPerParent,
        item.SortOrder, item.Notes, item.IsActive, item.Version, item.CreatedAt, item.UpdatedAt);
}

internal sealed record CaseComponentListResponse(
    IReadOnlyList<CaseComponentResponse> Items,
    string? NextCursor);

internal sealed record DerivedCaseOrderListResponse(
    IReadOnlyList<DerivedCaseOrder> Items,
    string? NextCursor);

internal sealed record CaseRequestIssue(string Field, string Code, string Message);

internal sealed class CaseRequestException : Exception
{
    internal CaseRequestException(IReadOnlyList<CaseRequestIssue> issues)
        : base("Case request is invalid.")
    {
        Issues = issues;
    }

    internal IReadOnlyList<CaseRequestIssue> Issues { get; }
}
