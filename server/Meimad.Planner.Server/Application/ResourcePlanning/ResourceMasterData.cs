using Meimad.Planner.Server.Application.EditMode;

namespace Meimad.Planner.Server.Application.ResourcePlanning;

internal sealed record SkillRecord(string Id, string Name, string? Description, bool IsActive, int Version);
internal sealed record WorkstationTypeRecord(string Id, string Name, string? Description, string PropertySchemaJson, bool IsActive, int Version);
internal sealed record WorkstationRecord(string Id, string Name, string WorkstationTypeId, string WorkingCalendarId,
    int Capacity, IReadOnlyList<string> Capabilities, string PropertiesJson, bool IsActive, int Version);
internal sealed record ExternalResourceRecord(string Id, string Name, string? SupplierName, int PromisedLeadTimeMinutes,
    int SafetyBufferMinutes, string LeadTimeSemantics, string? WorkingCalendarId, string PropertiesJson, bool IsActive, int Version);
internal sealed record OperationResourceRequirementRecord(string Id,string CaseOperationId,int SequencePosition,string ResourceClass,
    string? WorkstationTypeId,string? ExternalResourceId,string? RequiredCapability,string? RequiredSkillId,
    int CapacityRequired,int EstimatedDurationSeconds,string Direction,string? SimultaneousGroupKey,string? PredecessorRequirementId,bool IsActive,int Version);

internal interface IResourceMasterDataRepository
{
    Task<IReadOnlyList<SkillRecord>> ListSkillsAsync(CancellationToken token);
    Task<SkillRecord> CreateSkillAsync(SkillRecord value, EditAuthority authority, CancellationToken token);
    Task SetEmployeeSkillsAsync(string employeeId, IReadOnlyList<string> skillIds, EditAuthority authority, CancellationToken token);
    Task<IReadOnlyList<WorkstationTypeRecord>> ListWorkstationTypesAsync(CancellationToken token);
    Task<WorkstationTypeRecord> CreateWorkstationTypeAsync(WorkstationTypeRecord value, EditAuthority authority, CancellationToken token);
    Task<IReadOnlyList<WorkstationRecord>> ListWorkstationsAsync(CancellationToken token);
    Task<WorkstationRecord> CreateWorkstationAsync(WorkstationRecord value, EditAuthority authority, CancellationToken token);
    Task<IReadOnlyList<ExternalResourceRecord>> ListExternalResourcesAsync(CancellationToken token);
    Task<ExternalResourceRecord> CreateExternalResourceAsync(ExternalResourceRecord value, EditAuthority authority, CancellationToken token);
    Task<IReadOnlyList<OperationResourceRequirementRecord>> ListRequirementsAsync(string caseOperationId,CancellationToken token);
    Task<OperationResourceRequirementRecord> CreateRequirementAsync(OperationResourceRequirementRecord value,EditAuthority authority,CancellationToken token);
}

internal sealed class ResourceMasterDataService(IResourceMasterDataRepository repository)
{
    internal Task<IReadOnlyList<SkillRecord>> ListSkillsAsync(CancellationToken token = default) => repository.ListSkillsAsync(token);
    internal Task<IReadOnlyList<WorkstationTypeRecord>> ListWorkstationTypesAsync(CancellationToken token = default) => repository.ListWorkstationTypesAsync(token);
    internal Task<IReadOnlyList<WorkstationRecord>> ListWorkstationsAsync(CancellationToken token = default) => repository.ListWorkstationsAsync(token);
    internal Task<IReadOnlyList<ExternalResourceRecord>> ListExternalResourcesAsync(CancellationToken token = default) => repository.ListExternalResourcesAsync(token);
    internal Task<IReadOnlyList<OperationResourceRequirementRecord>> ListRequirementsAsync(string operationId,CancellationToken token=default)=>repository.ListRequirementsAsync(Required(operationId,"caseOperationId",200),token);

    internal Task<SkillRecord> CreateSkillAsync(string? name, string? description, EditAuthority authority, CancellationToken token = default) =>
        repository.CreateSkillAsync(new(Guid.NewGuid().ToString("N"), Required(name, "name", 120), Optional(description, 1000), true, 1), authority, token);

    internal Task<WorkstationTypeRecord> CreateWorkstationTypeAsync(string? name, string? description,
        string? propertySchemaJson, EditAuthority authority, CancellationToken token = default) =>
        repository.CreateWorkstationTypeAsync(new(Guid.NewGuid().ToString("N"), Required(name, "name", 120),
            Optional(description, 1000), JsonObject(propertySchemaJson, "propertySchema"), true, 1), authority, token);

    internal Task<WorkstationRecord> CreateWorkstationAsync(string? name, string? typeId, string? calendarId,
        int capacity, IReadOnlyList<string?>? capabilities, string? propertiesJson, EditAuthority authority,
        CancellationToken token = default) => repository.CreateWorkstationAsync(new(Guid.NewGuid().ToString("N"),
            Required(name, "name", 120), Required(typeId, "workstationTypeId", 200), Required(calendarId, "workingCalendarId", 200),
            capacity > 0 ? capacity : throw Invalid("capacity", "Capacity must be greater than zero."),
            NormalizeList(capabilities, "capabilities"), JsonObject(propertiesJson, "properties"), true, 1), authority, token);

    internal Task<ExternalResourceRecord> CreateExternalResourceAsync(string? name, string? supplierName,
        int leadMinutes, int bufferMinutes, string? semantics, string? calendarId, string? propertiesJson,
        EditAuthority authority, CancellationToken token = default)
    {
        if (leadMinutes < 0 || bufferMinutes < 0) throw Invalid("leadTime", "Lead time and buffer cannot be negative.");
        var normalizedSemantics = Required(semantics ?? "CALENDAR_TIME", "leadTimeSemantics", 40).ToUpperInvariant();
        if (normalizedSemantics is not ("CALENDAR_TIME" or "WORKING_TIME"))
            throw Invalid("leadTimeSemantics", "Use CALENDAR_TIME or WORKING_TIME.");
        return repository.CreateExternalResourceAsync(new(Guid.NewGuid().ToString("N"), Required(name, "name", 160),
            Optional(supplierName, 160), leadMinutes, bufferMinutes, normalizedSemantics, Optional(calendarId, 200),
            JsonObject(propertiesJson, "properties"), true, 1), authority, token);
    }

    internal Task SetEmployeeSkillsAsync(string employeeId, IReadOnlyList<string?>? skillIds,
        EditAuthority authority, CancellationToken token = default) => repository.SetEmployeeSkillsAsync(
            Required(employeeId, "employeeId", 200), NormalizeList(skillIds, "skillIds"), authority, token);

    internal Task<OperationResourceRequirementRecord> CreateRequirementAsync(string operationId,int position,string? resourceClass,
        string? workstationTypeId,string? externalResourceId,string? capability,string? skillId,int capacity,int durationSeconds,
        string? direction,string? groupKey,string? predecessorId,EditAuthority authority,CancellationToken token=default)
    {
        var kind=Required(resourceClass,"resourceClass",40).ToUpperInvariant();
        if(kind is not ("MACHINE" or "EMPLOYEE" or "WORKSTATION" or "EXTERNAL"))throw Invalid("resourceClass","Unknown base resource class.");
        var dir=Required(direction ?? "FORWARD","direction",20).ToUpperInvariant();
        if(dir is not ("BACKWARD" or "FORWARD"))throw Invalid("direction","Use BACKWARD or FORWARD.");
        if(position<0||capacity<1||durationSeconds<0)throw Invalid("requirement","Position/duration must be non-negative and capacity positive.");
        if(kind=="WORKSTATION"&&string.IsNullOrWhiteSpace(workstationTypeId))throw Invalid("workstationTypeId","A Workstation requirement needs a type.");
        if(kind=="EMPLOYEE"&&string.IsNullOrWhiteSpace(skillId))throw Invalid("requiredSkillId","An Employee requirement needs a Skill.");
        if(kind=="EXTERNAL"&&string.IsNullOrWhiteSpace(externalResourceId))throw Invalid("externalResourceId","An External requirement needs a service.");
        return repository.CreateRequirementAsync(new(Guid.NewGuid().ToString("N"),Required(operationId,"caseOperationId",200),position,kind,
            Optional(workstationTypeId,200),Optional(externalResourceId,200),Optional(capability,120),Optional(skillId,200),capacity,durationSeconds,dir,
            Optional(groupKey,120),Optional(predecessorId,200),true,1),authority,token);
    }

    private static string Required(string? value, string field, int max)
    {
        var result = value?.Trim();
        return string.IsNullOrWhiteSpace(result) || result.Length > max
            ? throw Invalid(field, $"{field} is required and may contain at most {max} characters.") : result;
    }
    private static string? Optional(string? value, int max)
    {
        var result = value?.Trim();
        if (string.IsNullOrEmpty(result)) return null;
        return result.Length > max ? throw Invalid("value", $"Value may contain at most {max} characters.") : result;
    }
    private static IReadOnlyList<string> NormalizeList(IReadOnlyList<string?>? values, string field) =>
        (values ?? []).Select(value => Required(value, field, 120)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    private static string JsonObject(string? value, string field)
    {
        var json = string.IsNullOrWhiteSpace(value) ? "{}" : value.Trim();
        try { using var document = System.Text.Json.JsonDocument.Parse(json); if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) throw new FormatException(); }
        catch (System.Text.Json.JsonException) { throw Invalid(field, $"{field} must be a JSON object."); }
        catch (FormatException) { throw Invalid(field, $"{field} must be a JSON object."); }
        return json;
    }
    private static ResourceMasterDataException Invalid(string field, string message) => new("resource_validation_failed", field, message);
}

internal sealed class ResourceMasterDataException(string code, string field, string message) : Exception(message)
{
    internal string Code { get; } = code;
    internal string Field { get; } = field;
}
