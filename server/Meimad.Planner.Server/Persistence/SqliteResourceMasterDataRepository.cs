using System.Globalization;
using System.Text.Json;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Application.ResourcePlanning;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqliteResourceMasterDataRepository(SqliteDatabase database) : IResourceMasterDataRepository
{
    public Task<IReadOnlyList<SkillRecord>> ListSkillsAsync(CancellationToken token) => ReadSkillsAsync(token);
    public Task<IReadOnlyList<WorkstationTypeRecord>> ListWorkstationTypesAsync(CancellationToken token) => ReadTypesAsync(token);
    public Task<IReadOnlyList<WorkstationRecord>> ListWorkstationsAsync(CancellationToken token) => ReadWorkstationsAsync(token);
    public Task<IReadOnlyList<ExternalResourceRecord>> ListExternalResourcesAsync(CancellationToken token) => ReadExternalAsync(token);
    public Task<IReadOnlyList<OperationResourceRequirementRecord>> ListRequirementsAsync(string operationId,CancellationToken token)=>ReadRequirementsAsync(operationId,token);

    public async Task<SkillRecord> CreateSkillAsync(SkillRecord value, EditAuthority authority, CancellationToken token)
    {
        await ExecuteCreateAsync("INSERT INTO skills(id,name,description,is_active,version,created_at,updated_at) " +
            "VALUES($id,$name,$description,1,1,$at,$at);", value.Id, value.Name, value.Description, authority, token);
        return value;
    }
    public async Task<WorkstationTypeRecord> CreateWorkstationTypeAsync(WorkstationTypeRecord value, EditAuthority authority, CancellationToken token)
    {
        await ExecuteCreateAsync("INSERT INTO workstation_types(id,name,description,property_schema_json,is_active,version,created_at,updated_at) " +
            "VALUES($id,$name,$description,$extra,1,1,$at,$at);", value.Id, value.Name, value.Description, authority, token, value.PropertySchemaJson);
        return value;
    }
    public async Task<WorkstationRecord> CreateWorkstationAsync(WorkstationRecord value, EditAuthority authority, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await EnsureEditAuthorityAsync(connection, transaction, authority, token);
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO workstations(id,name,workstation_type_id,working_calendar_id,capacity,capabilities_json,properties_json,is_active,version,created_at,updated_at) " +
            "VALUES($id,$name,$type,$calendar,$capacity,$capabilities,$properties,1,1,$at,$at);";
        command.Parameters.AddWithValue("$id", value.Id); command.Parameters.AddWithValue("$name", value.Name);
        command.Parameters.AddWithValue("$type", value.WorkstationTypeId); command.Parameters.AddWithValue("$calendar", value.WorkingCalendarId);
        command.Parameters.AddWithValue("$capacity", value.Capacity); command.Parameters.AddWithValue("$capabilities", JsonSerializer.Serialize(value.Capabilities));
        command.Parameters.AddWithValue("$properties", value.PropertiesJson); command.Parameters.AddWithValue("$at", Now());
        await ExecuteMappedAsync(command, token); await transaction.CommitAsync(token); return value;
    }
    public async Task<ExternalResourceRecord> CreateExternalResourceAsync(ExternalResourceRecord value, EditAuthority authority, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await EnsureEditAuthorityAsync(connection, transaction, authority, token);
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO external_resources(id,name,supplier_name,promised_lead_time_minutes,safety_buffer_minutes,lead_time_semantics,working_calendar_id,properties_json,is_active,version,created_at,updated_at) " +
            "VALUES($id,$name,$supplier,$lead,$buffer,$semantics,$calendar,$properties,1,1,$at,$at);";
        command.Parameters.AddWithValue("$id", value.Id); command.Parameters.AddWithValue("$name", value.Name);
        command.Parameters.AddWithValue("$supplier", Db(value.SupplierName)); command.Parameters.AddWithValue("$lead", value.PromisedLeadTimeMinutes);
        command.Parameters.AddWithValue("$buffer", value.SafetyBufferMinutes); command.Parameters.AddWithValue("$semantics", value.LeadTimeSemantics);
        command.Parameters.AddWithValue("$calendar", Db(value.WorkingCalendarId)); command.Parameters.AddWithValue("$properties", value.PropertiesJson);
        command.Parameters.AddWithValue("$at", Now()); await ExecuteMappedAsync(command, token); await transaction.CommitAsync(token); return value;
    }

    public async Task SetEmployeeSkillsAsync(string employeeId, IReadOnlyList<string> skillIds, EditAuthority authority, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await EnsureEditAuthorityAsync(connection, transaction, authority, token);
        await using (var clear = connection.CreateCommand()) { clear.Transaction = transaction; clear.CommandText = "DELETE FROM employee_skills WHERE employee_resource_id=$id;"; clear.Parameters.AddWithValue("$id", employeeId); await clear.ExecuteNonQueryAsync(token); }
        foreach (var skillId in skillIds)
        {
            await using var insert = connection.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO employee_skills(employee_resource_id,skill_id,assigned_at,assigned_by) VALUES($employee,$skill,$at,$by);";
            insert.Parameters.AddWithValue("$employee", employeeId); insert.Parameters.AddWithValue("$skill", skillId);
            insert.Parameters.AddWithValue("$at", Now()); insert.Parameters.AddWithValue("$by", authority.ClientId);
            await ExecuteMappedAsync(insert, token);
        }
        await transaction.CommitAsync(token);
    }

    public async Task<OperationResourceRequirementRecord> CreateRequirementAsync(OperationResourceRequirementRecord v,EditAuthority authority,CancellationToken token)
    {
        await using var c=await database.OpenConnectionAsync(token);await using var t=c.BeginTransaction(deferred:false);await EnsureEditAuthorityAsync(c,t,authority,token);
        await using var q=c.CreateCommand();q.Transaction=t;q.CommandText="""
            INSERT INTO operation_resource_requirements(id,case_operation_id,sequence_position,resource_class,workstation_type_id,external_resource_id,
                required_capability,required_skill_id,capacity_required,estimated_duration_seconds,direction,simultaneous_group_key,
                predecessor_requirement_id,is_active,version,created_at,updated_at)
            VALUES($id,$operation,$position,$class,$type,$external,$capability,$skill,$capacity,$duration,$direction,$group,$predecessor,1,1,$at,$at);
            """;
        q.Parameters.AddWithValue("$id",v.Id);q.Parameters.AddWithValue("$operation",v.CaseOperationId);q.Parameters.AddWithValue("$position",v.SequencePosition);
        q.Parameters.AddWithValue("$class",v.ResourceClass);q.Parameters.AddWithValue("$type",Db(v.WorkstationTypeId));q.Parameters.AddWithValue("$external",Db(v.ExternalResourceId));
        q.Parameters.AddWithValue("$capability",Db(v.RequiredCapability));q.Parameters.AddWithValue("$skill",Db(v.RequiredSkillId));q.Parameters.AddWithValue("$capacity",v.CapacityRequired);
        q.Parameters.AddWithValue("$duration",v.EstimatedDurationSeconds);q.Parameters.AddWithValue("$direction",v.Direction);q.Parameters.AddWithValue("$group",Db(v.SimultaneousGroupKey));
        q.Parameters.AddWithValue("$predecessor",Db(v.PredecessorRequirementId));q.Parameters.AddWithValue("$at",Now());await ExecuteMappedAsync(q,token);await t.CommitAsync(token);return v;
    }

    private async Task ExecuteCreateAsync(string sql, string id, string name, string? description, EditAuthority authority,
        CancellationToken token, string? extra = null)
    {
        await using var connection = await database.OpenConnectionAsync(token); await using var transaction = connection.BeginTransaction(deferred: false);
        await EnsureEditAuthorityAsync(connection, transaction, authority, token); await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = sql; command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$description", Db(description)); command.Parameters.AddWithValue("$at", Now());
        if (extra is not null) command.Parameters.AddWithValue("$extra", extra);
        await ExecuteMappedAsync(command, token); await transaction.CommitAsync(token);
    }

    private async Task<IReadOnlyList<SkillRecord>> ReadSkillsAsync(CancellationToken token)
    {
        await using var c = await database.OpenConnectionAsync(token); await using var q = c.CreateCommand(); q.CommandText = "SELECT id,name,description,is_active,version FROM skills ORDER BY name COLLATE NOCASE,id;";
        var values = new List<SkillRecord>(); await using var r = await q.ExecuteReaderAsync(token); while (await r.ReadAsync(token)) values.Add(new(r.GetString(0),r.GetString(1),Nullable(r,2),r.GetBoolean(3),r.GetInt32(4))); return values;
    }
    private async Task<IReadOnlyList<WorkstationTypeRecord>> ReadTypesAsync(CancellationToken token)
    {
        await using var c = await database.OpenConnectionAsync(token); await using var q = c.CreateCommand(); q.CommandText = "SELECT id,name,description,property_schema_json,is_active,version FROM workstation_types ORDER BY name COLLATE NOCASE,id;";
        var values = new List<WorkstationTypeRecord>(); await using var r = await q.ExecuteReaderAsync(token); while (await r.ReadAsync(token)) values.Add(new(r.GetString(0),r.GetString(1),Nullable(r,2),r.GetString(3),r.GetBoolean(4),r.GetInt32(5))); return values;
    }
    private async Task<IReadOnlyList<WorkstationRecord>> ReadWorkstationsAsync(CancellationToken token)
    {
        await using var c = await database.OpenConnectionAsync(token); await using var q = c.CreateCommand(); q.CommandText = "SELECT id,name,workstation_type_id,working_calendar_id,capacity,capabilities_json,properties_json,is_active,version FROM workstations ORDER BY name COLLATE NOCASE,id;";
        var values = new List<WorkstationRecord>(); await using var r = await q.ExecuteReaderAsync(token); while (await r.ReadAsync(token)) values.Add(new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetInt32(4),JsonSerializer.Deserialize<string[]>(r.GetString(5)) ?? [],r.GetString(6),r.GetBoolean(7),r.GetInt32(8))); return values;
    }
    private async Task<IReadOnlyList<ExternalResourceRecord>> ReadExternalAsync(CancellationToken token)
    {
        await using var c = await database.OpenConnectionAsync(token); await using var q = c.CreateCommand(); q.CommandText = "SELECT id,name,supplier_name,promised_lead_time_minutes,safety_buffer_minutes,lead_time_semantics,working_calendar_id,properties_json,is_active,version FROM external_resources ORDER BY name COLLATE NOCASE,id;";
        var values = new List<ExternalResourceRecord>(); await using var r = await q.ExecuteReaderAsync(token); while (await r.ReadAsync(token)) values.Add(new(r.GetString(0),r.GetString(1),Nullable(r,2),r.GetInt32(3),r.GetInt32(4),r.GetString(5),Nullable(r,6),r.GetString(7),r.GetBoolean(8),r.GetInt32(9))); return values;
    }
    private async Task<IReadOnlyList<OperationResourceRequirementRecord>> ReadRequirementsAsync(string operationId,CancellationToken token)
    {
        await using var c=await database.OpenConnectionAsync(token);await using var q=c.CreateCommand();q.CommandText="""
            SELECT id,case_operation_id,sequence_position,resource_class,workstation_type_id,external_resource_id,required_capability,
                   required_skill_id,capacity_required,estimated_duration_seconds,direction,simultaneous_group_key,predecessor_requirement_id,is_active,version
            FROM operation_resource_requirements WHERE case_operation_id=$id ORDER BY sequence_position,id;
            """;q.Parameters.AddWithValue("$id",operationId);var values=new List<OperationResourceRequirementRecord>();await using var r=await q.ExecuteReaderAsync(token);
        while(await r.ReadAsync(token))values.Add(new(r.GetString(0),r.GetString(1),r.GetInt32(2),r.GetString(3),Nullable(r,4),Nullable(r,5),Nullable(r,6),Nullable(r,7),r.GetInt32(8),r.GetInt32(9),r.GetString(10),Nullable(r,11),Nullable(r,12),r.GetBoolean(13),r.GetInt32(14)));return values;
    }

    private static async Task EnsureEditAuthorityAsync(SqliteConnection c, SqliteTransaction t, EditAuthority authority, CancellationToken token)
    {
        await SqliteEditModeRepository.ApplyExpiredRequestAsync(c,t,DateTimeOffset.UtcNow,token);
        await using var q = c.CreateCommand(); q.Transaction = t; q.CommandText = "SELECT holder_client_id,generation FROM edit_tokens WHERE id=1;";
        await using var reader=await q.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token) || reader.IsDBNull(0)) throw new EditModeMutationException("edit_mode_required", "No Windows client currently holds Edit Mode.");
        if (!string.Equals(reader.GetString(0),authority.ClientId,StringComparison.Ordinal) || reader.GetInt64(1)!=authority.Generation)
            throw new EditModeMutationException("edit_generation_stale", "This client does not hold the active Edit Mode generation.");
    }
    private static async Task ExecuteMappedAsync(SqliteCommand command, CancellationToken token)
    {
        try { await command.ExecuteNonQueryAsync(token); }
        catch (SqliteException e) when (e.SqliteErrorCode == 19) { throw new ResourceMasterDataException("resource_constraint_failed", "resource", "The resource name or referenced master data is invalid or already in use."); }
    }
    private static string Now() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
    private static object Db(object? value) => value ?? DBNull.Value;
    private static string? Nullable(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}
