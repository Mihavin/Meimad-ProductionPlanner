using System.IO.Compression;
using Meimad.Planner.Server.Application.GCode;

namespace Meimad.Planner.Server.Tests.GCode;

public sealed class NcVerificationMacroGeneratorTests
{
    /// <summary>Machine 10 (Haas VF-3SS) as configured in Setup: the commissioned V10 variable map.</summary>
    private static readonly NcVerificationMacroSettings HaasVf3ss =
        new(9001, 9002, 9003, 10501, 10500, 10502, 10503, 10504, 10, 6, 120, FromConfiguration: true);

    [Theory]
    [InlineData("O09001.nc")]
    [InlineData("O09002.nc")]
    [InlineData("O09003.nc")]
    public void Haas_rendering_reproduces_the_commissioned_v10_macros_byte_for_byte(string fileName)
    {
        var package = NcVerificationMacroGenerator.Generate(
            NcDialects.Profile(NcDialects.HaasNgc), HaasVf3ss, "10", "Haas VF-3ss");

        // The fixtures are the macros running on the VF-3SS today; only the event-ID tag differs.
        // The commissioned files end at the closing % with no final line break; the generated
        // files end every line, so the comparison ignores the final line ending.
        var expected = File.ReadAllText(Path.Combine(FixtureFolder(), fileName))
            .Replace("HAAS-VF3SS", "HAAS-10", StringComparison.Ordinal)
            .TrimEnd('\r', '\n');
        var generated = package.Files.Single(file => file.FileName == fileName).Text.TrimEnd('\r', '\n');
        Assert.Equal(expected, generated);
        Assert.Equal("HAAS-10", package.MachineTag);
        Assert.Contains("MACHINE 10 HAAS VF-3SS", package.Readme.ToUpperInvariant(), StringComparison.Ordinal);
        Assert.Contains("M109 P10500", package.Readme, StringComparison.Ordinal);
    }

    [Fact]
    public void Fanuc_and_mazak_macros_open_their_own_print_channel_and_read_the_code_from_a_variable()
    {
        var settings = new NcVerificationMacroSettings(9001, 9002, 9003, 501, 500, 502, 503, 504, 10, 6, 300, true);
        foreach (var dialectId in new[] { NcDialects.FanucMacroB, NcDialects.MazakMatrixEia })
        {
            var package = NcVerificationMacroGenerator.Generate(NcDialects.Profile(dialectId), settings, "08", "Doosan SVM-4100");
            Assert.Equal(["O9001.NC", "O9002.NC", "O9003.NC"], package.Files.Select(file => file.FileName));
            var challenge = package.Files[0].Text;
            var verify = package.Files[1].Text;
            var finalizer = package.Files[2].Text;
            var tag = dialectId == NcDialects.FanucMacroB ? "FANUC-08" : "MAZAK-08";

            Assert.StartsWith("%\r\nO9001 (MEIMAD PROTECTED CHALLENGE V10)\r\n", challenge, StringComparison.Ordinal);
            Assert.Contains($"POPEN\r\nDPRNT[MEIMAD/V/1/EVENT/OLC/ID/OLC-{tag}-#20[60]-#501[60]/SEQ/#30[60]/MACROVERSION/10/PROGRAM/#21[60]/OFFSETRELEASE/#20[60]/NONCE/#501[60]]\r\nPCLOS\r\n", challenge, StringComparison.Ordinal);
            Assert.Contains("#502=1.", challenge, StringComparison.Ordinal);
            Assert.Contains("#503=#20", challenge, StringComparison.Ordinal);
            Assert.DoesNotContain("G103", challenge, StringComparison.Ordinal);

            Assert.Contains("#3006=1 (MEIMAD CODE TO #500 THEN START)", verify, StringComparison.Ordinal);
            Assert.DoesNotContain("M109", verify, StringComparison.Ordinal);
            Assert.Contains($"POPEN\r\nDPRNT[MEIMAD/V/1/EVENT/SVR/ID/SVR-{tag}-", verify, StringComparison.Ordinal);
            Assert.Contains("G65 P9003 A#20 B#29 C#32 D#21 E#24 F#31", verify, StringComparison.Ordinal);
            Assert.Contains("#23=7919.", verify, StringComparison.Ordinal);
            Assert.Contains("#26=314159.", verify, StringComparison.Ordinal);
            Assert.Contains("IF [#31 GE #25] GOTO900", verify, StringComparison.Ordinal);

            Assert.Contains("IF [#27 GT 300000.] GOTO910", finalizer, StringComparison.Ordinal);
            Assert.Contains($"DPRNT[MEIMAD/V/1/EVENT/SVS/ID/SVS-{tag}-", finalizer, StringComparison.Ordinal);
            Assert.Contains($"DPRNT[MEIMAD/V/1/EVENT/SVF/ID/SVF-{tag}-", finalizer, StringComparison.Ordinal);
            Assert.Contains("#504=#30", finalizer, StringComparison.Ordinal);
            Assert.Contains("POPEN", package.Readme, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Okuma_macros_are_one_user_task_library_with_put_write_and_call()
    {
        var settings = new NcVerificationMacroSettings(9001, 9002, 9003, 1, 2, 3, 4, 5, 10, 6, 120, true);
        var package = NcVerificationMacroGenerator.Generate(NcDialects.Profile(NcDialects.OkumaOsp), settings, "09", "Okuma L200E-M");

        var library = Assert.Single(package.Files);
        Assert.Equal("MEIMAD.SUB", library.FileName);
        var text = library.Text;
        Assert.Contains("O9001\r\n(MEIMAD PROTECTED CHALLENGE V10", text, StringComparison.Ordinal);
        Assert.Contains("O9002\r\n(MEIMAD PROTECTED VERIFY INPUT V10", text, StringComparison.Ordinal);
        Assert.Contains("O9003\r\n(MEIMAD PROTECTED FINALIZER V10", text, StringComparison.Ordinal);
        Assert.Contains("PUT 'MEIMAD/V/1/EVENT/OLC/ID/OLC-OKUMA-09-'\r\nPUT PA,6,0\r\nPUT '-'\r\nPUT VC1,6,0\r\nPUT '/SEQ/'\r\nPUT VC190,6,0\r\nPUT '/MACROVERSION/10/PROGRAM/'\r\nPUT PB,6,0\r\nPUT '/OFFSETRELEASE/'\r\nPUT PA,6,0\r\nPUT '/NONCE/'\r\nPUT VC1,6,0\r\nWRITE C\r\nRTS", text, StringComparison.Ordinal);
        Assert.Contains("CALL O9003 PA=PA PB=VC196 PC=VC197 PE=VC198 PF=VC191", text, StringComparison.Ordinal);
        Assert.Contains("VC5=VC190", text, StringComparison.Ordinal);
        Assert.Contains("M00", text, StringComparison.Ordinal);
        Assert.True(text.Split("RTS\r\n").Length - 1 >= 3, "every subprogram returns with RTS");
        Assert.DoesNotContain("DPRNT", text, StringComparison.Ordinal);
        Assert.DoesNotContain("G65", text, StringComparison.Ordinal);
        Assert.DoesNotContain("#", text, StringComparison.Ordinal);
        Assert.Contains("VC190-VC199", package.Readme, StringComparison.Ordinal);
    }

    [Fact]
    public void Defaults_follow_the_specification_and_are_marked_in_the_readme()
    {
        var fanuc = NcVerificationMacroGenerator.DefaultSettings(NcDialects.Profile(NcDialects.FanucMacroB));
        Assert.Equal((501, 500, 502, 503, 504, 10), (fanuc.NonceVariable, fanuc.ResponseVariable, fanuc.VerificationStateVariable, fanuc.ReleaseTokenVariable, fanuc.EventSequenceVariable, fanuc.MacroVersion));
        Assert.False(fanuc.FromConfiguration);
        Assert.Equal(10504, NcVerificationMacroGenerator.DefaultSettings(NcDialects.Profile(NcDialects.HaasNgc)).EventSequenceVariable);
        Assert.Equal(5, NcVerificationMacroGenerator.DefaultSettings(NcDialects.Profile(NcDialects.OkumaOsp)).EventSequenceVariable);
        Assert.Equal(10504, NcDialects.Profile(NcDialects.HaasNgc).DefaultEventSequenceVariable);
        Assert.Equal(504, NcDialects.Profile(NcDialects.MazakMatrixEia).DefaultEventSequenceVariable);

        var package = NcVerificationMacroGenerator.Generate(NcDialects.Profile(NcDialects.FanucMacroB), fanuc, "01", "NM-510");
        Assert.Contains("DIALECT DEFAULTS", package.Readme, StringComparison.Ordinal);
        Assert.Equal("FANUC-01", package.MachineTag);
    }

    [Fact]
    public void Zip_holds_every_file_and_the_readme()
    {
        var package = NcVerificationMacroGenerator.Generate(NcDialects.Profile(NcDialects.HaasNgc), HaasVf3ss, "10", "Haas VF-3ss");

        using var archive = new ZipArchive(new MemoryStream(NcVerificationMacroZip.Build(package)), ZipArchiveMode.Read);

        Assert.Equal(["O09001.nc", "O09002.nc", "O09003.nc", "README.txt"], archive.Entries.Select(entry => entry.FullName));
        using var reader = new StreamReader(archive.GetEntry("O09002.nc")!.Open());
        Assert.Equal(package.Files[1].Text, reader.ReadToEnd());
    }

    private static string FixtureFolder()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
        {
            directory = directory.Parent;
        }
        return Path.Combine(directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found."),
            "tests", "fixtures", "verification-macros", "haas-v10");
    }
}
