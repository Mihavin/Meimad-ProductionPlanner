using System.Security.Cryptography;
using System.Text;

namespace Meimad.Planner.Server.Backup;

internal static class RecoveryCommand
{
    internal static async Task<int?> TryRunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is not ("--recovery-password-file" or "--recovery-restore")) return null;
        try
        {
            var password = Environment.GetEnvironmentVariable("MEIMAD_RECOVERY_PASSWORD")
                ?? throw new InvalidOperationException("Set MEIMAD_RECOVERY_PASSWORD in this process before running the offline recovery command.");
            Environment.SetEnvironmentVariable("MEIMAD_RECOVERY_PASSWORD", null);
            RecoveryEncryption.ValidatePassword(password);
            if (args[0] == "--recovery-password-file")
            {
                if (args.Length != 2) throw new InvalidOperationException("Usage: --recovery-password-file <new absolute file path>");
                var path = Path.GetFullPath(args[1]);
                RecoveryPaths.RejectLinks(path);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var plain = Encoding.UTF8.GetBytes(password);
                try
                {
                    using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    RecoveryPaths.ProtectFile(path);
                    file.Write(RecoveryKeys.Protect(plain));
                    file.Flush(true);
                }
                finally { CryptographicOperations.ZeroMemory(plain); }
                Console.WriteLine("Machine-protected recovery password file created. Keep the recovery passphrase separately for a replacement PC.");
            }
            else
            {
                if (args.Length != 4 || args[2] != "--restore-to")
                    throw new InvalidOperationException("Usage: --recovery-restore <archive.mprb> --restore-to <new directory>");
                var restored = await RecoverySetService.RestoreAsync(Path.GetFullPath(args[1]), password, args[3], CancellationToken.None);
                Console.WriteLine($"Recovery set {restored.Manifest.SetId} restored and verified in {restored.Directory}. {restored.ActivationNotice}");
            }
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Recovery operation failed ({exception.GetType().Name}). {exception.Message}");
            return 1;
        }
    }
}
