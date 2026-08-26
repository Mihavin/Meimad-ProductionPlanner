using Meimad.Planner.Server.Application.Anomalies;
using Meimad.Planner.Server.Persistence;
using Meimad.Planner.Server.Tests.Persistence;
using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Tests.Anomalies;

public sealed class OperationalAnomalyTests
{
    [Fact]
    public async Task Catalog_tracks_every_required_type_idempotently_and_is_immutable()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        var repository = new SqliteOperationalAnomalyRepository(fixture.Database);
        var service = new OperationalAnomalyService(repository);
        var at = DateTimeOffset.Parse("2026-08-26T22:00:00Z");

        foreach (var type in OperationalAnomalyTypes.All)
        {
            var value = new AppendOperationalAnomaly(
                type, "TEST", $"test:{type}", at, SourceEventId: type);
            await service.AppendAsync(value);
            await service.AppendAsync(value);
        }

        var values = await service.ListAsync(null, null, null, 100);
        Assert.Equal(17, values.Count);
        Assert.Equal(
            OperationalAnomalyTypes.All.Order(StringComparer.Ordinal),
            values.Select(value => value.AnomalyType).Order(StringComparer.Ordinal));
        Assert.All(values, value => Assert.False(string.IsNullOrWhiteSpace(value.Message)));
        Assert.All(values, value => Assert.Equal("{}", value.DetailsJson));
        Assert.StartsWith("CNC VERIFICATION MACRO UPDATE REQUIRED",
            Assert.Single(values, value =>
                value.AnomalyType == "verification_macro_version_mismatch").Message,
            StringComparison.Ordinal);

        await using var connection = await fixture.Database.OpenConnectionAsync();
        await using var update = connection.CreateCommand();
        update.CommandText = "UPDATE operational_anomalies SET source='CHANGED';";
        var updateError = await Assert.ThrowsAsync<SqliteException>(
            () => update.ExecuteNonQueryAsync());
        Assert.Contains("immutable", updateError.Message, StringComparison.OrdinalIgnoreCase);

        await using var delete = connection.CreateCommand();
        delete.CommandText = "DELETE FROM operational_anomalies;";
        var deleteError = await Assert.ThrowsAsync<SqliteException>(
            () => delete.ExecuteNonQueryAsync());
        Assert.Contains("immutable", deleteError.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task List_filters_and_validation_are_bounded()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await using (var connection = await fixture.Database.OpenConnectionAsync())
        await using (var setup = connection.CreateCommand())
        {
            setup.CommandText = """
                INSERT INTO working_calendars(id,name,time_zone_id)
                VALUES('anomaly-calendar','Calendar','UTC');
                INSERT INTO machines(id,number,name,machine_type,working_calendar_id,status,is_active)
                VALUES('anomaly-machine','1','Machine','mill','anomaly-calendar','active',1);
                """;
            await setup.ExecuteNonQueryAsync();
        }
        var service = new OperationalAnomalyService(
            new SqliteOperationalAnomalyRepository(fixture.Database));
        await service.AppendAsync(new(
            "wrong_nc_program", "TEST", "filtered", DateTimeOffset.UtcNow,
            "anomaly-machine"));

        var filtered = await service.ListAsync(
            "anomaly-machine", null, "wrong_nc_program", 1);
        Assert.Single(filtered);
        Assert.Equal("anomaly-machine", filtered[0].MachineId);

        await Assert.ThrowsAsync<OperationalAnomalyValidationException>(
            () => service.ListAsync(null, null, "not_real", 10));
        await Assert.ThrowsAsync<OperationalAnomalyValidationException>(
            () => service.ListAsync(null, null, null, 1001));
    }

    [Fact]
    public async Task Revoking_tablet_credential_creates_one_immutable_operational_anomaly()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await using var connection = await fixture.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO device_registry(
                id,device_type,device_name,credential_hash,is_enabled,tablet_id,
                hardware_id,version,created_at,updated_at)
            VALUES('anomaly-tablet','eink','Anomaly tablet','hash',1,'7001',
                   'AA:BB:CC:DD:EE:70',1,$at,$at);
            UPDATE device_registry
            SET is_enabled=0,version=2,updated_at=$at
            WHERE id='anomaly-tablet';
            UPDATE device_registry
            SET is_enabled=0,version=3,updated_at=$at
            WHERE id='anomaly-tablet';
            """;
        command.Parameters.AddWithValue("$at", "2026-08-26T22:00:00Z");
        await command.ExecuteNonQueryAsync();
        command.CommandText = """
            SELECT COUNT(*) FROM operational_anomalies
            WHERE anomaly_type='tablet_credential_revoked'
              AND tablet_device_id='anomaly-tablet';
            """;
        command.Parameters.Clear();
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
    }
}
