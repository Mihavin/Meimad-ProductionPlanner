using System.IO.Compression;
using System.Text;

namespace Meimad.Planner.Server.Application.GCode;

/// <summary>Packs a generated macro package as a zip: the subprogram files plus README.txt.</summary>
internal static class NcVerificationMacroZip
{
    internal static byte[] Build(NcVerificationMacroPackage package)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in package.Files) Write(archive, file.FileName, file.Text);
            Write(archive, "README.txt", package.Readme);
        }
        return stream.ToArray();
    }

    private static void Write(ZipArchive archive, string name, string text)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(text);
    }
}
