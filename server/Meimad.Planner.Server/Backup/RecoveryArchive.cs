using System.Security.Cryptography;
using System.Text;

namespace Meimad.Planner.Server.Backup;

// Sequential entries avoid buffering whole archives or writing unencrypted temporary archives.
internal static class RecoveryArchive
{
    internal const long MaximumBytes = 1L << 40;
    internal static async Task<RecoveryFile> WriteEntryAsync(Stream output, string name, Stream input, long length, CancellationToken token)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        using (var writer = new BinaryWriter(output, Encoding.UTF8, true))
        { writer.Write(nameBytes.Length); writer.Write(nameBytes); writer.Write(length); }
        var hash = await CopyAsync(input, output, length, token);
        if (input.ReadByte() != -1) throw new InvalidDataException("Recovery source changed while copying.");
        return new(name, length, hash);
    }

    internal static async Task<string> CopyAsync(Stream input, Stream output, long length, CancellationToken token)
    {
        if (length < 0 || length > MaximumBytes) throw new InvalidDataException("Recovery entry exceeds supported size.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        for (var remaining = length; remaining > 0;)
        {
            var count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token);
            if (count == 0) throw new EndOfStreamException("Recovery content is incomplete.");
            await output.WriteAsync(buffer.AsMemory(0, count), token);
            hash.AppendData(buffer, 0, count);
            remaining -= count;
        }
        CryptographicOperations.ZeroMemory(buffer);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    internal static (string Name, long Length)? ReadHeader(Stream input)
    {
        using var reader = new BinaryReader(input, Encoding.UTF8, true);
        var count = reader.ReadInt32();
        if (count == 0) return null;
        if (count is < 1 or > 4096) throw new InvalidDataException("Invalid recovery entry name.");
        var nameBytes = reader.ReadBytes(count);
        if (nameBytes.Length != count) throw new EndOfStreamException();
        var name = new UTF8Encoding(false, true).GetString(nameBytes);
        var length = reader.ReadInt64();
        if (length < 0 || length > MaximumBytes) throw new InvalidDataException("Invalid recovery entry size.");
        return (name, length);
    }

    internal static void End(Stream output)
    { using var writer = new BinaryWriter(output, Encoding.UTF8, true); writer.Write(0); }

    internal static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(file, token));
    }
}
