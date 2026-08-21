using Meimad.Planner.Server.Application.GCode;

namespace Meimad.Planner.Server.Tests.GCode;

public sealed class ReleasedToolTableParserTests
{
    [Theory]
    [InlineData("30p450025601-001_nc1.DIR/FANUC_4X/TP_MODEL.TOOLS.mht", 8)]
    [InlineData("30p450025601-001_nc2.DIR/Haas_UMC_5axTTCB_RTCP-Plus/TP_MODEL.TOOLS.mht", 10)]
    public async Task Real_Cimatron_exports_are_parsed_without_conversion(
        string relativePath,
        int expectedCount)
    {
        var root = FindRepositoryRoot();
        var path = Path.Combine(
            root,
            "data",
            "sample data for testing",
            "UTILL",
            relativePath.Replace('/', Path.DirectorySeparatorChar));

        var parsed = await ReleasedToolTableParser.ParseAsync(
            path,
            Path.GetFileName(path),
            CancellationToken.None);

        Assert.Equal(expectedCount, parsed.RequiredToolCount);
        Assert.Equal(expectedCount, parsed.Tools.Count);
        Assert.All(parsed.Tools, tool =>
        {
            Assert.Matches("^T[0-9]+$", tool.ToolIdentifier);
            Assert.True(tool.IsRequired);
            Assert.True(tool.RequiresMagazinePosition);
            Assert.True(tool.IsActive);
            Assert.Null(tool.MagazinePosition);
        });
    }

    [Fact]
    public async Task Quoted_printable_Cimatron_names_are_decoded()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "MeimadPlanner.MhtParser.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "TP_MODEL.TOOLS.mht");
        try
        {
            await File.WriteAllTextAsync(path, """
                MIME-Version: 1.0
                Content-Type: multipart/related; boundary="cam-boundary"

                --cam-boundary
                Content-Transfer-Encoding: quoted-printable
                Content-Type: text/html; charset="us-ascii"

                <html><body><table class=3DMsoTableGrid>
                <tr><td>Number</td><td>Name</td><td>Dia</td></tr>
                <tr><td><b>T1</b></td><td>FLYCUTTER=5F80</td><td>80.</td></tr>
                <tr><td><b>T17</b></td><td>DRILL=5F8.5</td><td>8.5</td></tr>
                </table></body></html>
                --cam-boundary--
                """, CancellationToken.None);

            var parsed = await ReleasedToolTableParser.ParseAsync(
                path,
                Path.GetFileName(path),
                CancellationToken.None);

            Assert.Equal(2, parsed.RequiredToolCount);
            Assert.Collection(
                parsed.Tools,
                tool =>
                {
                    Assert.Equal("T1", tool.ToolIdentifier);
                    Assert.Equal("FLYCUTTER_80", tool.Description);
                },
                tool =>
                {
                    Assert.Equal("T17", tool.ToolIdentifier);
                    Assert.Equal("DRILL_8.5", tool.Description);
                });
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "server"))
                && Directory.Exists(Path.Combine(directory.FullName, "data", "sample data for testing")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository sample-data root.");
    }
}
