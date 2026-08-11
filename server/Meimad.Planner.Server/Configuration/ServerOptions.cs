using System.Net;
using System.Net.Sockets;

namespace Meimad.Planner.Server.Configuration;

public sealed class ServerOptions
{
    public const string SectionName = "Server";

    public string Host { get; init; } = "127.0.0.1";

    public int Port { get; init; } = 5080;

    public string ServiceName { get; init; } = "Meimad Planner Server";

    public static ServerOptions FromConfiguration(IConfiguration configuration)
    {
        var options = configuration
            .GetSection(SectionName)
            .Get<ServerOptions>()
            ?? new ServerOptions();

        options.Validate();
        return options;
    }

    public string GetListenUrl()
    {
        var host = Host.Trim();

        if (IPAddress.TryParse(host, out var address)
            && address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            host = $"[{address}]";
        }

        return $"http://{host}:{Port}";
    }

    private void Validate()
    {
        var host = Host.Trim();

        if (host.Length == 0)
        {
            throw new InvalidOperationException("Server:Host must not be empty.");
        }

        if (host.Contains("://", StringComparison.Ordinal)
            || host.Contains('/', StringComparison.Ordinal)
            || host.Contains('\\', StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Server:Host must be a host name, IP address, localhost, '*' or '+', without a URL scheme or path.");
        }

        var isWildcard = host is "*" or "+";
        var isIpAddress = IPAddress.TryParse(host.Trim('[', ']'), out _);
        var isHostName = Uri.CheckHostName(host) is UriHostNameType.Dns;

        if (!isWildcard && !isIpAddress && !isHostName)
        {
            throw new InvalidOperationException(
                $"Server:Host '{Host}' is not a valid host name or IP address.");
        }

        if (Port is < 1 or > 65535)
        {
            throw new InvalidOperationException("Server:Port must be between 1 and 65535.");
        }

        if (string.IsNullOrWhiteSpace(ServiceName))
        {
            throw new InvalidOperationException("Server:ServiceName must not be empty.");
        }
    }
}
