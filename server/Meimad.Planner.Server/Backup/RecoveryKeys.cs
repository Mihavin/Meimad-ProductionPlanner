using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meimad.Planner.Server.Backup;

internal static class RecoveryKeys
{
    internal const string ApplicationName = "Meimad.Planner.Server";
    internal const string ProbePurpose = "Meimad.Planner.Recovery.Probe.v1";

    internal static byte[] Export(IKeyManager manager)
    {
        var root = new XElement("recoveryKeys");
        foreach (var key in manager.GetAllKeys())
        {
            var descriptor = key.Descriptor.ExportToXml();
            root.Add(new XElement("key", new XAttribute("id", key.KeyId), new XAttribute("version", 1),
                new XElement("creationDate", key.CreationDate), new XElement("activationDate", key.ActivationDate),
                new XElement("expirationDate", key.ExpirationDate), new XElement("descriptor",
                    new XAttribute("deserializerType", descriptor.DeserializerType.AssemblyQualifiedName!),
                    descriptor.SerializedDescriptorElement)));
            if (key.IsRevoked)
                root.Add(new XElement("revocation", new XAttribute("version", 1),
                    new XElement("revocationDate", DateTimeOffset.UtcNow), new XElement("key", new XAttribute("id", key.KeyId)),
                    new XElement("reason", "Revoked state preserved at recovery export.")));
        }
        if (!root.Elements("key").Any()) throw new InvalidDataException("No recoverable Data Protection keys are available.");
        return Encoding.UTF8.GetBytes(root.ToString(SaveOptions.DisableFormatting));
    }

    internal static ServiceProvider OpenProvider(byte[] exported)
    {
        var repository = new MemoryKeys(XElement.Parse(Encoding.UTF8.GetString(exported)).Elements().Select(x => new XElement(x)).ToList());
        var services = new ServiceCollection();
        services.AddDataProtection().SetApplicationName(ApplicationName).DisableAutomaticKeyGeneration();
        services.Configure<KeyManagementOptions>(options => options.XmlRepository = repository);
        return services.BuildServiceProvider();
    }

    internal static void Install(byte[] exported, string folder)
    {
        RecoveryPaths.CreatePrivateDirectory(folder);
        var repository = new DpapiRecoveryKeyRepository(folder);
        foreach (var element in XElement.Parse(Encoding.UTF8.GetString(exported)).Elements())
            repository.StoreElement(element, Guid.NewGuid().ToString("N"));
    }

    internal static string ReadPassword(string path)
    {
        RecoveryPaths.RejectLinks(path);
        var bytes = Unprotect(File.ReadAllBytes(path));
        try { return Encoding.UTF8.GetString(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    internal static byte[] Protect(byte[] value)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Recovery key storage requires Windows DPAPI.");
        var encrypted = new DpapiXmlEncryptor(true, NullLoggerFactory.Instance).Encrypt(new XElement("secret", Convert.ToBase64String(value)));
        return Encoding.UTF8.GetBytes(encrypted.EncryptedElement.ToString(SaveOptions.DisableFormatting));
    }

    private static byte[] Unprotect(byte[] value)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Recovery key storage requires Windows DPAPI.");
        var decrypted = new DpapiXmlDecryptor().Decrypt(XElement.Parse(Encoding.UTF8.GetString(value)));
        return Convert.FromBase64String(decrypted.Value);
    }

    internal sealed class DpapiRecoveryKeyRepository(string folder) : IXmlRepository
    {
        public IReadOnlyCollection<XElement> GetAllElements()
        {
            RecoveryPaths.RejectLinks(folder);
            if (!Directory.Exists(folder)) return [];
            var result = new List<XElement>();
            foreach (var path in Directory.EnumerateFiles(folder, "*.key"))
            {
                RecoveryPaths.RejectLinks(path);
                var plain = Unprotect(File.ReadAllBytes(path));
                try { result.Add(XElement.Parse(Encoding.UTF8.GetString(plain))); }
                finally { CryptographicOperations.ZeroMemory(plain); }
            }
            return result;
        }

        public void StoreElement(XElement element, string friendlyName)
        {
            RecoveryPaths.RejectLinks(folder);
            Directory.CreateDirectory(folder);
            var plain = Encoding.UTF8.GetBytes(element.ToString(SaveOptions.DisableFormatting));
            try
            {
                var encrypted = Protect(plain);
                var path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".key");
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                stream.Write(encrypted);
                stream.Flush(true);
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
    }

    private sealed class MemoryKeys(List<XElement> elements) : IXmlRepository
    {
        public IReadOnlyCollection<XElement> GetAllElements() => elements;
        public void StoreElement(XElement element, string friendlyName) => throw new InvalidOperationException("Recovery validation cannot generate keys.");
    }
}
