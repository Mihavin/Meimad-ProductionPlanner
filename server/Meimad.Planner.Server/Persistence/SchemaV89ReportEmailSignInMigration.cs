using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>
/// Mailbox sign-in for report email (owner decision 2026-09-28): the SMTP user name and the password,
/// encrypted with the Server's Data Protection keys so that only this Server can read it. Clients
/// never receive the password; they only see whether one is saved.
/// </summary>
internal sealed class SchemaV89ReportEmailSignInMigration : IDatabaseMigration
{
    public int Version => 89;

    public string Name => "report_email_sign_in";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE report_email_settings ADD COLUMN smtp_user_name TEXT NULL
                CHECK (smtp_user_name IS NULL OR length(trim(smtp_user_name)) BETWEEN 1 AND 320);
            ALTER TABLE report_email_settings ADD COLUMN smtp_password_protected TEXT NULL
                CHECK (smtp_password_protected IS NULL OR length(smtp_password_protected) > 0);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
