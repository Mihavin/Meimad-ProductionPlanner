using System.Globalization;
using System.Text.Json;
using Meimad.Planner.Server.Domain.Readiness;
using Meimad.Planner.Server.Domain.Timeline;
using Meimad.Planner.Server.Persistence;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Application.ProductionPackages;

internal sealed record HandoffWorker(string EmployeeId, string Name, string QualificationStamp);
internal sealed record PackageHandoff(string HandoffId, string PackageId, string EmployeeId, string EmployeeName,
    string Actor, DateTimeOffset RecordedAt, int Version, string QualificationStamp, ProductionPackageContext Context);
internal sealed record HandoffView(string PackageId, int Version, bool PackageIsCurrent, bool HandoffIsCurrent,
    IReadOnlyList<HandoffWorker> QualifiedWorkers, IReadOnlyList<PackageHandoff> History);
internal sealed record HandoffRequest(string RequestId, string EmployeeId, string QualificationStamp,
    int ExpectedVersion, ProductionPackageContext Context);

internal sealed class PackageHandoffService(SqliteDatabase database, TimeProvider clock)
{
    internal async Task<HandoffView> ReadAsync(string packageId, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var tx = connection.BeginTransaction(deferred: true);
        var saved = await PackageContextAsync(connection, tx, packageId, token);
        var live = (await SqliteProductionPackageContext.ListAsync(connection, tx, saved.BatchOperationId, token))
            .SingleOrDefault(x => x.MachineAssignmentId == saved.MachineAssignmentId && x.ProductionRunProgramId == saved.ProductionRunProgramId && x.ProductionRunOutputId == saved.ProductionRunOutputId);
        var current = live is not null && (await SqliteProductionPackageRepository.ReadCurrentAsync(connection, tx, live, token))?.ProductionPackageId == packageId;
        var workers = await WorkersAsync(connection, tx, saved.MachineId, token);
        var history = await HistoryAsync(connection, tx, saved, token);
        var latest = history.FirstOrDefault();
        return new(packageId, latest?.Version ?? 0, current, current && latest is not null && latest.PackageId == packageId
            && latest.Context.ContextStamp == live!.ContextStamp && workers.Any(x => x.EmployeeId == latest.EmployeeId && x.QualificationStamp == latest.QualificationStamp),
            workers, history);
    }
    internal async Task<PackageHandoff> RecordAsync(string packageId, HandoffRequest request, string actor, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Length > 128 || request.Context is null
            || string.IsNullOrWhiteSpace(request.Context.ContextStamp) || request.ExpectedVersion < 0)
            throw new ArgumentException("Provide an idempotency key, exact current context and observed handoff version.");
        var fingerprint = ProductionActionPolicy.Stamp(new { packageId, request.EmployeeId, request.QualificationStamp, request.ExpectedVersion, request.Context, actor });
        await using var connection = await database.OpenConnectionAsync(token);
        await using var tx = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand(); command.Transaction = tx;
        command.CommandText = "SELECT request_fingerprint,id,package_id,employee_id,employee_name,actor,created_at,sequence,qualification_stamp,context_json FROM production_package_handoffs WHERE request_id=$request";
        command.Parameters.AddWithValue("$request", request.RequestId);
        await using (var reader = await command.ExecuteReaderAsync(token))
            if (await reader.ReadAsync(token))
            {
                if (reader.GetString(0) != fingerprint) throw new InvalidOperationException("Handoff request ID was already used for a different request.");
                return ReadRow(reader, 1);
            }
        var live = await SqliteProductionPackageContext.ResolveAsync(connection, tx, request.Context.BatchOperationId, request.Context.Selection, token)
            ?? throw new InvalidOperationException("Execution context is no longer current.");
        var package = await SqliteProductionPackageRepository.ReadCurrentAsync(connection, tx, live, token);
        if (package?.ProductionPackageId != packageId) throw new InvalidOperationException("Package changed or is no longer valid. Refresh before recording physical handoff.");
        var workers = await WorkersAsync(connection, tx, live.MachineId, token);
        var worker = workers.SingleOrDefault(x => x.EmployeeId == request.EmployeeId && x.QualificationStamp == request.QualificationStamp)
            ?? throw new InvalidOperationException("Operator qualification changed or is not valid for this Machine. Refresh the qualified operator list.");
        var history = await HistoryAsync(connection, tx, live, token);
        if ((history.FirstOrDefault()?.Version ?? 0) != request.ExpectedVersion) throw new InvalidOperationException("Another handoff was recorded. Refresh and review its actor and time.");
        var result = new PackageHandoff(Guid.NewGuid().ToString("N"), packageId, worker.EmployeeId, worker.Name, actor,
            clock.GetUtcNow(), request.ExpectedVersion + 1, worker.QualificationStamp, live);
        command.Parameters.Clear();
        command.CommandText = """
            INSERT INTO production_package_handoffs VALUES($id,$request,$actor,$at,$package,$employee,$name,$qualification,$context,$assignment,$program,$output,$version,$fingerprint);
            INSERT INTO structured_event_log(id,event_type,occurred_at,user_id,related_entity_ids_json,reason_code)
                VALUES($id,'physical_package_handoff_recorded',$at,$actor,$related,'tracking_only');
            """;
        command.Parameters.AddWithValue("$id", result.HandoffId); command.Parameters.AddWithValue("$request", request.RequestId);
        command.Parameters.AddWithValue("$actor", actor); command.Parameters.AddWithValue("$at", result.RecordedAt.ToString("O"));
        command.Parameters.AddWithValue("$package", packageId); command.Parameters.AddWithValue("$employee", worker.EmployeeId); command.Parameters.AddWithValue("$name", worker.Name);
        command.Parameters.AddWithValue("$qualification", worker.QualificationStamp); command.Parameters.AddWithValue("$context", JsonSerializer.Serialize(live));
        command.Parameters.AddWithValue("$assignment", live.MachineAssignmentId); command.Parameters.AddWithValue("$program", live.ProductionRunProgramId); command.Parameters.AddWithValue("$output", live.ProductionRunOutputId);
        command.Parameters.AddWithValue("$version", result.Version); command.Parameters.AddWithValue("$fingerprint", fingerprint);
        command.Parameters.AddWithValue("$related", JsonSerializer.Serialize(new { productionPackageId = packageId, productionRunId = live.ProductionRunId, machineId = live.MachineId, batchOperationId = live.BatchOperationId }));
        await command.ExecuteNonQueryAsync(token); await tx.CommitAsync(token); return result;
    }
    private static async Task<ProductionPackageContext> PackageContextAsync(SqliteConnection connection, SqliteTransaction tx, string packageId, CancellationToken token)
    {
        await using var command = connection.CreateCommand(); command.Transaction = tx;
        command.CommandText = "SELECT context_json FROM production_package_contexts WHERE production_package_id=$id"; command.Parameters.AddWithValue("$id", packageId);
        return await command.ExecuteScalarAsync(token) is string json ? JsonSerializer.Deserialize<ProductionPackageContext>(json)!
            : throw new ArgumentException("Package has no recorded exact execution context.");
    }
    private static async Task<IReadOnlyList<HandoffWorker>> WorkersAsync(SqliteConnection connection, SqliteTransaction tx, string machineId, CancellationToken token)
    {
        await using var command = connection.CreateCommand(); command.Transaction = tx;
        command.CommandText = """
            SELECT m.id,m.number,m.name,m.machine_type,m.axis_type,m.capabilities_json,t.capabilities_json,m.version
            FROM machines m LEFT JOIN machine_types t ON t.id=m.machine_type_id WHERE m.id=$id AND m.is_active=1;
            """;
        command.Parameters.AddWithValue("$id", machineId);
        var skills = new List<string>(); var machineVersion = 0;
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            if (!await reader.ReadAsync(token)) return [];
            for (var i = 0; i <= 4; i++) if (!reader.IsDBNull(i)) skills.Add(reader.GetString(i));
            for (var i = 5; i <= 6; i++) if (!reader.IsDBNull(i)) skills.AddRange(JsonSerializer.Deserialize<string[]>(reader.GetString(i)) ?? []);
            machineVersion = reader.GetInt32(7);
        }
        command.Parameters.Clear();
        command.CommandText = "SELECT id,name,version,skills_json FROM employee_resources WHERE is_active=1 AND resource_type='setup_worker' ORDER BY name,id";
        var workers = new List<HandoffWorker>();
        await using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token))
            {
                var employeeSkills = JsonSerializer.Deserialize<string[]>(reader.GetString(3)) ?? [];
                if (SetupWorkerQualification.Matches(employeeSkills, skills)) workers.Add(new(reader.GetString(0), reader.GetString(1),
                    ProductionActionPolicy.Stamp(new { machineId, machineVersion, skills = skills.OrderBy(x => x, StringComparer.Ordinal), employee = reader.GetString(0), version = reader.GetInt32(2), employeeSkills })));
            }
        return workers;
    }
    private static async Task<IReadOnlyList<PackageHandoff>> HistoryAsync(SqliteConnection connection, SqliteTransaction tx, ProductionPackageContext context, CancellationToken token)
    {
        await using var command = connection.CreateCommand(); command.Transaction = tx;
        command.CommandText = "SELECT id,package_id,employee_id,employee_name,actor,created_at,sequence,qualification_stamp,context_json FROM production_package_handoffs WHERE machine_assignment_id=$assignment AND program_id=$program AND output_id=$output ORDER BY sequence DESC LIMIT 100";
        command.Parameters.AddWithValue("$assignment", context.MachineAssignmentId); command.Parameters.AddWithValue("$program", context.ProductionRunProgramId); command.Parameters.AddWithValue("$output", context.ProductionRunOutputId);
        var rows = new List<PackageHandoff>(); await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) rows.Add(ReadRow(reader, 0)); return rows;
    }
    private static PackageHandoff ReadRow(SqliteDataReader reader, int start) => new(reader.GetString(start), reader.GetString(start + 1), reader.GetString(start + 2), reader.GetString(start + 3),
        reader.GetString(start + 4), DateTimeOffset.Parse(reader.GetString(start + 5), CultureInfo.InvariantCulture), reader.GetInt32(start + 6), reader.GetString(start + 7),
        JsonSerializer.Deserialize<ProductionPackageContext>(reader.GetString(start + 8))!);
}
