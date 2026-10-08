namespace Meimad.Planner.Server.Application.ProductionPackages;

/// <summary>The generated-artifact boundary; source releases remain separately read-only.</summary>
internal class ProductionPackageFiles
{
    internal virtual Task WriteAsync(string path, byte[] bytes, CancellationToken token)
        => File.WriteAllBytesAsync(path, bytes, token);

    internal virtual void Move(string source, string destination) => Directory.Move(source, destination);

    internal virtual Stream OpenForPublication(string path)
        => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
}
