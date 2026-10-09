using Meimad.Planner.Server.Application.Projections;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SqliteProjectionRevisionReader(SqliteDatabase database, bool includeOperationalEvidence = false) : IProjectionRevisionReader
{
    public async Task<string> ReadAsync(CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = includeOperationalEvidence
            ? "SELECT epoch || ':evidence:' || revision FROM projection_revision WHERE id=1"
            : "SELECT epoch || ':planning:' || (revision - non_planning_revision) FROM projection_revision WHERE id=1";
        return (string?)await command.ExecuteScalarAsync(token)
            ?? throw new InvalidDataException("Projection revision is missing; repair the Server database before reading projections.");
    }
}
