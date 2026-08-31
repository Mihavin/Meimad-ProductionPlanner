using Meimad.Planner.Server.Application.Haas;
using Meimad.Planner.Server.Domain.Haas;
using Meimad.Planner.Server.Infrastructure.Haas;

namespace Meimad.Planner.Server.Tests.Haas;

public sealed class LocalNetShareHaasProgramReaderTests
{
    [Fact]
    public async Task MtConnect_filename_requires_exact_file_and_matching_o_number()
    {
        var root = Directory.CreateTempSubdirectory("MeimadPlanner.HaasShare.");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "1500.CNC"),
                "%\r\nO1500 (30P283003300-002_NC1)\r\n");
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "1501.CNC"),
                "%\r\nO1501 (WRONG-PART_NC1)\r\n");

            var header = await new LocalNetShareHaasProgramReader(TimeProvider.System)
                .ReadActiveProgramHeaderAsync(Settings(root.FullName), "1500.CNC");
            var metadata = new NcHeaderParser().Parse(header.FirstLines);

            Assert.Equal("O1500", header.ProgramNumber);
            Assert.EndsWith("1500.CNC", header.SourcePath, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("30P283003300-002", metadata.PartName);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task MtConnect_filename_rejects_a_file_whose_header_o_number_does_not_match()
    {
        var root = Directory.CreateTempSubdirectory("MeimadPlanner.HaasShare.");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "1500.CNC"),
                "%\nO1501 (WRONG-PART_NC1)\n");

            var error = await Assert.ThrowsAsync<HaasProgramHeaderUnavailableException>(() =>
                new LocalNetShareHaasProgramReader(TimeProvider.System)
                    .ReadActiveProgramHeaderAsync(Settings(root.FullName), "1500.CNC"));

            Assert.Contains("1500.CNC", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static HaasConnectionSettings Settings(string path) => new(
        "machine-haas", "192.168.0.56", "44:B1:76:B0:26:68", 5051, 8082, 8080, true, path, null,
        HaasPartCounterSources.M30Counter1, 2000, 3000, 2,
        50, 32768, NcHeaderParser.DefaultPartPatterns, true, 1,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, HaasTelemetryProviders.MtConnect);
}
