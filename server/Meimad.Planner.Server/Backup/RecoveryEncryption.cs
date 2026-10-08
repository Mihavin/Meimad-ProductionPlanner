using System.Security.Cryptography;
using System.Text;

namespace Meimad.Planner.Server.Backup;

// Encrypt-then-MAC: independent AES-256-CBC and HMAC-SHA256 keys, authenticated header/ciphertext.
// Authenticate the entire archive before decrypting anything. No plaintext archive is staged on disk.
internal static class RecoveryEncryption
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("MEIMAD-RECOVERY1");
    private const int HeaderLength = 64;
    private const int Iterations = 600_000;

    internal static async Task WriteAsync(string path, string password, Func<Stream, Task> write, CancellationToken token)
    {
        ValidatePassword(password);
        var salt = RandomNumberGenerator.GetBytes(32);
        var keys = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 64);
        try
        {
            using var aes = Aes.Create();
            aes.Key = keys[..32];
            aes.GenerateIV();
            await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, true);
            await file.WriteAsync(Magic, token);
            await file.WriteAsync(salt, token);
            await file.WriteAsync(aes.IV, token);
            await using (var crypto = new CryptoStream(file, aes.CreateEncryptor(), CryptoStreamMode.Write, true))
            {
                await write(crypto);
                await crypto.FlushFinalBlockAsync(token);
            }
            var length = file.Length;
            file.Position = 0;
            using var mac = new HMACSHA256(keys[32..]);
            var tag = await mac.ComputeHashAsync(new LimitedStream(file, length), token);
            file.Position = length;
            await file.WriteAsync(tag, token);
            await file.FlushAsync(token);
            file.Flush(true);
        }
        finally { CryptographicOperations.ZeroMemory(keys); }
    }

    internal static async Task ReadAsync(string path, string password, Func<Stream, Task> read, CancellationToken token)
    {
        ValidatePassword(password);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        if (file.Length < HeaderLength + 16 + 32) throw new InvalidDataException("Recovery archive is incomplete.");
        var header = new byte[HeaderLength];
        await file.ReadExactlyAsync(header, token);
        if (!header.AsSpan(0, 16).SequenceEqual(Magic)) throw new InvalidDataException("Unsupported recovery archive format.");
        var keys = Rfc2898DeriveBytes.Pbkdf2(password, header.AsSpan(16, 32), Iterations, HashAlgorithmName.SHA256, 64);
        try
        {
            var length = file.Length - 32;
            file.Position = 0;
            using var mac = new HMACSHA256(keys[32..]);
            var expected = await mac.ComputeHashAsync(new LimitedStream(file, length), token);
            var actual = new byte[32];
            await file.ReadExactlyAsync(actual, token);
            if (!CryptographicOperations.FixedTimeEquals(expected, actual))
                throw new CryptographicException("Wrong recovery password or damaged recovery archive.");
            using var aes = Aes.Create();
            aes.Key = keys[..32];
            aes.IV = header[48..64];
            file.Position = HeaderLength;
            await using var crypto = new CryptoStream(new LimitedStream(file, length - HeaderLength), aes.CreateDecryptor(), CryptoStreamMode.Read);
            await read(crypto);
            if (crypto.ReadByte() != -1) throw new InvalidDataException("Unexpected recovery archive content.");
        }
        finally { CryptographicOperations.ZeroMemory(keys); }
    }

    internal static void ValidatePassword(string password)
    {
        if (password.Length is < 16 or > 1024) throw new InvalidOperationException("Use a recovery passphrase of 16 to 1024 characters.");
    }

    private sealed class LimitedStream(Stream inner, long remaining) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        { var n = inner.Read(buffer, offset, (int)Math.Min(count, remaining)); remaining -= n; return n; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { var n = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, remaining)], token); remaining -= n; return n; }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
