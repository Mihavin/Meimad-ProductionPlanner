namespace Meimad.CimatronExporter;

/// <summary>
/// A short local folder Cimatron writes into before the files go where the package wants them. Meimad, 2026-10-13: three
/// posts of the Haas UMC toolpath into the package folder on the share
/// (<c>\\192.168.0.240\data\customers files\…\.staging\…\nc</c>, about 200 characters with spaces) stopped inside Cimatron's
/// post-processor after it wrote <c>TEMP_POST\fort.28</c>, and Cimatron froze; every earlier post that worked went to a short
/// local folder, as Cimatron's own post dialog does (<c>%TEMP%</c>).
/// </summary>
internal sealed class CimatronWorkFolder : IDisposable
{
    /// <summary>The post-processor's own working files, never part of the result.</summary>
    internal const string PostWorkingFolder = "TEMP_POST";

    private CimatronWorkFolder(string path) => Path = path;

    public string Path { get; }

    public static CimatronWorkFolder Create(string? root = null) =>
        new(Directory.CreateDirectory(System.IO.Path.Combine(root ?? System.IO.Path.GetTempPath(), "MCX", Guid.NewGuid().ToString("N")[..8])).FullName);

    /// <summary>
    /// Copies the files Cimatron wrote below this folder into <paramref name="target"/>, keeping their subfolders (the posted
    /// NC can sit in "&lt;document&gt;.DIR\&lt;post&gt;\") and leaving out <see cref="PostWorkingFolder"/>; a file outside this
    /// folder is returned unchanged. Returns the paths in <paramref name="target"/>.
    /// </summary>
    public IReadOnlyList<string> CopyTo(IEnumerable<string> files, string target)
    {
        var result = new List<string>();
        foreach (var file in files)
        {
            var full = System.IO.Path.GetFullPath(file);
            if (!IsBelow(full))
            {
                result.Add(file);
                continue;
            }

            var relative = System.IO.Path.GetRelativePath(Path, full);
            if (relative.Split(System.IO.Path.DirectorySeparatorChar)
                .Any(part => part.Equals(PostWorkingFolder, StringComparison.OrdinalIgnoreCase))) continue;
            var destination = System.IO.Path.Combine(target, relative);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
            File.Copy(full, destination, overwrite: true);
            result.Add(destination);
        }

        return result;
    }

    /// <summary>Copies one file this folder holds to <paramref name="destination"/>, when Cimatron wrote it.</summary>
    public void CopyFile(string name, string destination)
    {
        var source = System.IO.Path.Combine(Path, name);
        if (!File.Exists(source)) return;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: true);
    }

    /// <summary>Copies every file below this folder into <paramref name="target"/>, keeping subfolders.</summary>
    public void CopyAllTo(string target) =>
        CopyTo(Directory.Exists(Path) ? Directory.GetFiles(Path, "*", SearchOption.AllDirectories) : [], target);

    private bool IsBelow(string full) =>
        full.StartsWith(System.IO.Path.TrimEndingDirectorySeparator(Path) + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A file Cimatron still holds stays in %TEMP%; the package has its copy.
        }
    }
}
