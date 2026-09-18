using Meimad.Planner.Server.Application.Cnc;
using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Persistence;
using Meimad.Planner.Server.Tests.Persistence;

namespace Meimad.Planner.Server.Tests.Cnc;

public sealed class NcDialectVerificationSettingsTests
{
    private static readonly EditAuthority Authority = new("verification-client", 1);

    [Fact]
    public async Task Variable_ranges_follow_the_Machine_NC_dialect()
    {
        await using var fixture = await TemporaryDatabase.CreateAsync();
        await CncVerificationFoundationTests.SeedAsync(fixture.Database);
        await using (var connection = await fixture.Database.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO machines(id,number,name,machine_type,working_calendar_id,status,is_active,nc_dialect)
                VALUES('machine-osp','12','Okuma','lathe','calendar-verification','active',1,'OKUMA_OSP');
                INSERT INTO machines(id,number,name,machine_type,working_calendar_id,status,is_active,nc_dialect)
                VALUES('machine-fanuc','13','Fanuc','mill','calendar-verification','active',1,'FANUC_MACRO_B');
                """;
            await command.ExecuteNonQueryAsync();
        }
        var service = new CncVerificationFoundationService(
            new SqliteCncVerificationFoundationRepository(fixture.Database), TimeProvider.System);

        // Haas keeps the v61 rule.
        var haasRejected = await Assert.ThrowsAsync<CncVerificationValidationException>(() =>
            service.UpdateSettingsAsync("machine-verification", Settings(1, 2, 3, 4, 5), 0, Authority));
        Assert.Equal("out_of_range", haasRejected.Code);
        Assert.Equal("nonceVariable", haasRejected.Field);

        // Okuma OSP: common variables VC1-VC200, response variable in the same range.
        var osp = await service.UpdateSettingsAsync("machine-osp", Settings(1, 2, 3, 4, 5), 0, Authority);
        Assert.Equal(5, osp.EventSequenceVariable);
        var ospRejected = await Assert.ThrowsAsync<CncVerificationValidationException>(() =>
            service.UpdateSettingsAsync("machine-osp", Settings(10501, 500, 10502, 10503, 10504), 1, Authority));
        Assert.Equal("out_of_range", ospRejected.Code);
        var ospResponse = await Assert.ThrowsAsync<CncVerificationValidationException>(() =>
            service.UpdateSettingsAsync("machine-osp", Settings(1, 500, 3, 4, 5), 1, Authority));
        Assert.Equal("unsupported_m109_variable", ospResponse.Code);
        Assert.Contains("VC1-VC200", ospResponse.Message, StringComparison.Ordinal);

        // FANUC macro B: #500-#999 for every mapping, no M109 alias, collisions still rejected.
        var fanuc = await service.UpdateSettingsAsync("machine-fanuc", Settings(501, 505, 502, 503, 504), 0, Authority);
        Assert.Equal(505, fanuc.ResponseVariable);
        var fanucCollision = await Assert.ThrowsAsync<CncVerificationValidationException>(() =>
            service.UpdateSettingsAsync("machine-fanuc", Settings(501, 501, 502, 503, 504), 1, Authority));
        Assert.Equal("variable_collision", fanucCollision.Code);
        var fanucRejected = await Assert.ThrowsAsync<CncVerificationValidationException>(() =>
            service.UpdateSettingsAsync("machine-fanuc", Settings(10501, 500, 10502, 10503, 10504), 1, Authority));
        Assert.Equal("out_of_range", fanucRejected.Code);
    }

    private static UpdateCncVerificationSettings Settings(int nonce, int response, int state, int release, int sequence) =>
        new("HAAS_DPRNT_TCP", 8080, 9001, 9002, 605, nonce, response, state, release, 9003, sequence, 6, 6, 300, false);
}
