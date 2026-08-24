using Meimad.Planner.Server.Application.ProductionRuns;
using Meimad.Planner.Server.Domain.ProductionRuns;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqliteProductionRunToolingRepository(SqliteDatabase database) : IProductionRunToolingRepository
{
    public async Task<ProductionRunToolingFacts> ReadAsync(string runId, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        int? capacity = null;
        await using (var query = connection.CreateCommand())
        {
            query.CommandText = """
                SELECT machine.usable_tool_positions
                FROM machine_assignments assignment JOIN machines machine ON machine.id=assignment.machine_id
                WHERE assignment.production_run_id=$id;
                """;
            query.Parameters.AddWithValue("$id", runId);
            var value = await query.ExecuteScalarAsync(token);
            if (value is not null and not DBNull) capacity = Convert.ToInt32(value);
        }
        var tools = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        await using (var query = connection.CreateCommand())
        {
            query.CommandText = """
                SELECT lower(trim(tool.tool_identifier)), COALESCE(trim(tool.magazine_position),'')
                FROM production_run_programs program
                JOIN process_revisions revision ON revision.id=program.process_revision_id
                JOIN tool_table_release_tools tool ON tool.tool_table_release_id=revision.tool_table_release_id
                WHERE program.production_run_id=$id AND tool.requires_magazine_position=1;
                """;
            query.Parameters.AddWithValue("$id", runId);
            await using var reader = await query.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var tool = reader.GetString(0); var position = reader.GetString(1);
                if (!tools.TryGetValue(tool, out var positions)) tools[tool] = positions = new(StringComparer.OrdinalIgnoreCase);
                positions.Add(position);
            }
        }
        var positionOwners = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in tools)
            foreach (var position in pair.Value.Where(value => value.Length > 0))
            {
                if (!positionOwners.TryGetValue(position, out var owners)) positionOwners[position] = owners = new(StringComparer.OrdinalIgnoreCase);
                owners.Add(pair.Key);
            }
        var conflicts = new List<string>();
        conflicts.AddRange(tools.Where(value => value.Value.Count > 1)
            .Select(value => $"Tool '{value.Key}' is assigned to conflicting positions: {string.Join(", ", value.Value.Order())}."));
        conflicts.AddRange(positionOwners.Where(value => value.Value.Count > 1)
            .Select(value => $"Magazine position '{value.Key}' is assigned to conflicting tools: {string.Join(", ", value.Value.Order())}."));
        return new(capacity, tools.Count, conflicts);
    }
}
