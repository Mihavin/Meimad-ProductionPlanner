using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

/// <summary>
/// The BT40 pull studs of the published standards in the Setup library (owner request 2026-09-29):
/// MAS 403 P40T-1/-2/-3 (45°/60°/90°) solid and with a Ø4 coolant hole, JIS B6339-40P (15°) and
/// the Mazak BT40 stud, from the Jimmore/JIC pull stud catalog (pp. 45–46) and the Showa Tool MAS
/// table. Exposed length is L1 (flange face to the top of the knob). The seeded HAAS BT40 stud is
/// corrected to the same L1 (it was given the flange-to-grip length L2) unless someone has edited it.
/// Entries whose name already exists are left alone.
/// </summary>
internal sealed class SchemaV92Bt40PullStudsMigration : IDatabaseMigration
{
    public int Version => 92;

    public string Name => "bt40_pull_studs";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO pull_studs (
                id, name, thread, angle, overall_length, exposed_length, knob_diameter,
                neck_diameter, pilot_diameter, notes, updated_at, updated_by)
            VALUES
                ('mas-p40t-1', 'MAS 403 P40T-1 45°', 'M16x2.0', 45, 60, 35, 15, 10, 23,
                 'MAS 403 type I, solid. L 60, L1 35, L2 28, flange D 23, D1 17, knob D2 15, SW 19. Source: JIC/Jimmore catalog p.45, Showa Tool.',
                 strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), 'system'),
                ('mas-p40t-2', 'MAS 403 P40T-2 60°', 'M16x2.0', 60, 60, 35, 15, 10, 23,
                 'MAS 403 type II, solid. L 60, L1 35, L2 28, flange D 23, D1 17, knob D2 15, SW 19. Source: JIC/Jimmore catalog p.45, Showa Tool.',
                 strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), 'system'),
                ('mas-p40t-3', 'MAS 403 P40T-3 90°', 'M16x2.0', 90, 60, 35, 15, 10, 23,
                 'MAS 403 type III, solid. L 60, L1 35, L2 28, flange D 23, D1 17, knob D2 15, SW 19. Source: JIC/Jimmore catalog p.45.',
                 strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), 'system'),
                ('mas-p40t-1h', 'MAS 403 P40T-1H 45° (coolant)', 'M16x2.0', 45, 60, 35, 15, 10, 23,
                 'MAS 403 type I with a Ø4 coolant hole. L 60, L1 35, L2 28, flange D 23, D1 17, SW 19. Source: JIC/Jimmore catalog p.45.',
                 strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), 'system'),
                ('mas-p40t-2h', 'MAS 403 P40T-2H 60° (coolant)', 'M16x2.0', 60, 60, 35, 15, 10, 23,
                 'MAS 403 type II with a Ø4 coolant hole. L 60, L1 35, L2 28, flange D 23, D1 17, SW 19. Source: JIC/Jimmore catalog p.45.',
                 strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), 'system'),
                ('mas-p40t-3h', 'MAS 403 P40T-3H 90° (coolant)', 'M16x2.0', 90, 60, 35, 15, 10, 23,
                 'MAS 403 type III with a Ø4 coolant hole. L 60, L1 35, L2 28, flange D 23, D1 17, SW 19. Source: JIC/Jimmore catalog p.45.',
                 strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), 'system'),
                ('jis-b6339-40p', 'JIS B6339-40P 15° (coolant)', 'M16x2.0', 15, 54, 29, 17, NULL, 23,
                 'JIS B6339 type P for MAS BT tool holders, Ø7 coolant hole. L 54, L1 29, L2 23, flange D 23, D1 17 (taken as the knob), SW 19; neck not published. Source: JIC/Jimmore catalog p.46.',
                 strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), 'system'),
                ('mazak-bt40-45', 'Mazak BT40 45° (coolant)', 'M16x2.0', 45, 44.1, 19.1, 17, NULL, 22,
                 'Mazak BT40 pull stud, Ø7 through hole. L 44.1, L1 19.1, L2 14, D 22, D1 17 (taken as the knob); neck not published. Source: JIC/Jimmore catalog p.45.',
                 strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), 'system');

            -- The HAAS BT40 stud has the MAS 403 P40T-1 dimensions: L1 35 above the holder, flange D 23.
            UPDATE pull_studs
            SET exposed_length = 34.93, pilot_diameter = 23,
                notes = 'Retention knob for Haas BT40 spindles (same as MAS 403 P40T-1). L 59.94, L1 34.93 (flange to top), L2 27.94, knob 14.96, neck 9.96, flange D 23. Source: Shars, JIC/Jimmore catalog.',
                version = version + 1, updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
            WHERE id = 'haas-bt40-45-m16' AND version = 1 AND updated_by = 'system';
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
