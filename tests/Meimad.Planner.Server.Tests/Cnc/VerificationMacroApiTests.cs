using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Meimad.Planner.Server.Tests.Cnc;

public sealed class VerificationMacroApiTests
{
    [Fact]
    public async Task Macro_packages_are_rendered_per_machine_from_its_configuration_or_the_dialect_defaults()
    {
        var root = Path.Combine(Path.GetTempPath(), "MeimadPlanner.VerificationMacros.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await using var application = ServerApplication.Build(
            ["--Server:Host=127.0.0.1", "--Server:Port=5098", $"--Database:Path={Path.Combine(root, "test.db")}"],
            webHost => webHost.UseTestServer());
        try
        {
            await application.StartAsync();
            await SeedAsync(application.Services);
            using var client = application.GetTestClient();

            // Haas: the configured variable map; the files match the commissioned V10 macros.
            using var haas = await client.GetAsync("/api/v1/machines/machine-haas/verification-macros?format=json");
            Assert.Equal(HttpStatusCode.OK, haas.StatusCode);
            using var haasJson = JsonDocument.Parse(await haas.Content.ReadAsStringAsync());
            var haasRoot = haasJson.RootElement;
            Assert.True(haasRoot.GetProperty("fromConfiguration").GetBoolean());
            Assert.Equal("HAAS_NGC", haasRoot.GetProperty("ncDialect").GetString());
            Assert.Equal("HAAS-10", haasRoot.GetProperty("machineTag").GetString());
            Assert.Equal(10, haasRoot.GetProperty("macroVersion").GetInt32());
            var haasFiles = haasRoot.GetProperty("files").EnumerateArray().ToArray();
            Assert.Equal(["O09001.nc", "O09002.nc", "O09003.nc"], haasFiles.Select(file => file.GetProperty("fileName").GetString()));
            Assert.Contains("DPRNT[MEIMAD/V/1/EVENT/OLC/ID/OLC-HAAS-10-#20[60]-#10501[60]/SEQ/#30[60]/MACROVERSION/10/PROGRAM/#21[60]/OFFSETRELEASE/#20[60]/NONCE/#10501[60]]",
                haasFiles[0].GetProperty("text").GetString(), StringComparison.Ordinal);
            Assert.Contains("N101 M109 P10500 (MEIMAD DIGIT 1 OF 6)", haasFiles[1].GetProperty("text").GetString(), StringComparison.Ordinal);

            // FANUC without a configuration: dialect defaults, marked as such.
            using var fanuc = await client.GetAsync("/api/v1/machines/machine-fanuc/verification-macros?format=json");
            Assert.Equal(HttpStatusCode.OK, fanuc.StatusCode);
            using var fanucJson = JsonDocument.Parse(await fanuc.Content.ReadAsStringAsync());
            Assert.False(fanucJson.RootElement.GetProperty("fromConfiguration").GetBoolean());
            Assert.Equal("FANUC-08", fanucJson.RootElement.GetProperty("machineTag").GetString());
            var fanucFiles = fanucJson.RootElement.GetProperty("files").EnumerateArray().ToArray();
            Assert.Equal(["O9001.NC", "O9002.NC", "O9003.NC"], fanucFiles.Select(file => file.GetProperty("fileName").GetString()));
            Assert.Contains("#3006=1 (MEIMAD CODE TO #500 THEN START)", fanucFiles[1].GetProperty("text").GetString(), StringComparison.Ordinal);
            Assert.Contains("DIALECT DEFAULTS", fanucJson.RootElement.GetProperty("readme").GetString(), StringComparison.Ordinal);

            // The zip download carries the same files plus the README.
            using var zip = await client.GetAsync("/api/v1/machines/machine-okuma/verification-macros");
            Assert.Equal(HttpStatusCode.OK, zip.StatusCode);
            Assert.Equal("application/zip", zip.Content.Headers.ContentType?.MediaType);
            Assert.Contains("meimad-verification-macros-09-OKUMA_OSP-v10.zip", zip.Content.Headers.ContentDisposition?.ToString(), StringComparison.Ordinal);
            using var archive = new ZipArchive(new MemoryStream(await zip.Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read);
            Assert.Equal(["MEIMAD.SUB", "README.txt"], archive.Entries.Select(entry => entry.FullName));
            using var reader = new StreamReader(archive.GetEntry("MEIMAD.SUB")!.Open());
            Assert.Contains("PUT 'MEIMAD/V/1/EVENT/OLC/ID/OLC-OKUMA-09-'", await reader.ReadToEndAsync(), StringComparison.Ordinal);

            using var missing = await client.GetAsync("/api/v1/machines/no-such-machine/verification-macros");
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
        finally
        {
            await application.StopAsync();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    private static async Task SeedAsync(IServiceProvider services)
    {
        await using var connection = await services.GetRequiredService<SqliteDatabase>().OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO working_calendars(id,name,time_zone_id,calendar_json)
            VALUES('calendar-macros','Macro Calendar','UTC','{}');
            INSERT INTO machines(id,number,name,machine_type,working_calendar_id,status,is_active,
                                 display_enabled,execution_mode,nc_dialect)
            VALUES('machine-haas','10','Haas VF-3ss','mill','calendar-macros','active',1,1,'CNC_GCODE','HAAS_NGC'),
                  ('machine-fanuc','08','Doosan SVM-4100','mill','calendar-macros','active',1,1,'CNC_GCODE','FANUC_MACRO_B'),
                  ('machine-okuma','09','Okuma L200E-M','lathe','calendar-macros','active',1,1,'CNC_GCODE','OKUMA_OSP');
            INSERT INTO cnc_verification_settings(
                machine_id,dprint_transport,dprint_port,challenge_program_number,verify_program_number,
                custom_gcode_alias,nonce_variable,response_variable,verification_state_variable,
                release_token_variable,expected_macro_version,response_code_digits,verification_timeout_seconds,
                enabled,version,created_at,updated_at,finalize_program_number,event_sequence_variable)
            VALUES('machine-haas','HAAS_DPRNT_TCP',8080,9001,9002,NULL,10501,10500,10502,10503,
                   10,6,120,0,1,'2026-09-01T08:00:00Z','2026-09-01T08:00:00Z',9003,10504);
            """;
        await command.ExecuteNonQueryAsync();
    }
}
