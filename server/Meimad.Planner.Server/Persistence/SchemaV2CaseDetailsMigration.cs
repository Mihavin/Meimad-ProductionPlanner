using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SchemaV2CaseDetailsMigration : IDatabaseMigration
{
    public int Version => 2;

    public string Name => "case_details";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE cases ADD COLUMN customer TEXT;
            ALTER TABLE cases ADD COLUMN material_type TEXT;
            ALTER TABLE cases ADD COLUMN material_specification TEXT;
            ALTER TABLE cases ADD COLUMN raw_material_form TEXT;
            ALTER TABLE cases ADD COLUMN raw_material_dimensions TEXT;
            ALTER TABLE cases ADD COLUMN notes TEXT;

            UPDATE cases
            SET material_specification = material
            WHERE material_specification IS NULL AND material IS NOT NULL;

            UPDATE cases
            SET raw_material_dimensions = raw_stock
            WHERE raw_material_dimensions IS NULL AND raw_stock IS NOT NULL;
            """;

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
