using System.Security.Cryptography;
using System.Text;

namespace Meimad.Planner.Client.Windows.Configuration;

internal sealed record ClientSettings(
    Uri ServerBaseUri,
    string LocalUserName,
    string ClientId)
{
    internal const string DefaultServerAddress = "http://127.0.0.1:5080/";

    internal string LocalUserId
    {
        get
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(LocalUserName));
            return $"local-{Convert.ToHexStringLower(hash)[..24]}";
        }
    }

    internal static ClientSettings Create(
        string? serverAddress,
        string? localUserName,
        string? clientId)
    {
        var address = serverAddress?.Trim();
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.AbsolutePath != "/"
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ClientSettingsException(
                "Server address must be an absolute HTTP or HTTPS server root without credentials, path, query, or fragment.");
        }

        var normalizedUserName = localUserName?.Trim();
        if (string.IsNullOrEmpty(normalizedUserName) || normalizedUserName.Length > 200)
        {
            throw new ClientSettingsException(
                "Local user name must contain between 1 and 200 characters.");
        }

        var normalizedClientId = clientId?.Trim();
        if (string.IsNullOrEmpty(normalizedClientId) || normalizedClientId.Length > 200)
        {
            throw new ClientSettingsException(
                "Client ID must contain between 1 and 200 characters.");
        }

        var builder = new UriBuilder(uri)
        {
            Path = uri.AbsolutePath.TrimEnd('/') + "/"
        };
        return new ClientSettings(builder.Uri, normalizedUserName, normalizedClientId);
    }

    internal static ClientSettings Default() => Create(
        DefaultServerAddress,
        string.IsNullOrWhiteSpace(Environment.UserName) ? "Planner" : Environment.UserName,
        $"windows-{Guid.NewGuid():N}");
}

internal sealed class ClientSettingsException : Exception
{
    internal ClientSettingsException(string message)
        : base(message)
    {
    }
}
