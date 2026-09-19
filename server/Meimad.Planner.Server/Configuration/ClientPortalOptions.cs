namespace Meimad.Planner.Server.Configuration;

/// <summary>
/// Push of customer-safe Order status to the cloud customer portal (the separate
/// <c>meimad-client-cloud-portal</c> project). Disabled by default: nothing leaves
/// the Server unless <c>ClientPortal:Enabled</c> is true and at least one customer
/// is mapped. The Server needs no Google credential for this; it only holds the
/// portal's ingest shared secret, which can call one ingest endpoint and nothing else.
/// Which Customers are pushed is no longer configured here: that mapping lives in the
/// <c>client_portal_customers</c> table and is managed from the Windows client's Setup screen.
/// </summary>
public sealed class ClientPortalOptions
{
    public const string SectionName = "ClientPortal";

    public bool Enabled { get; init; }

    /// <summary>Full ingest URL, e.g. https://meimad-ingest-….run.app/ingest/orders.</summary>
    public string IngestUrl { get; init; } = string.Empty;

    /// <summary>The shared secret configured on the ingest service. Prefer a file so it is not in appsettings.json.</summary>
    public string SharedSecret { get; init; } = string.Empty;

    /// <summary>Path of a file whose trimmed contents are the shared secret; used when SharedSecret is empty.</summary>
    public string SharedSecretFile { get; init; } = string.Empty;

    public int PollIntervalSeconds { get; init; } = 300;

    public int RequestTimeoutSeconds { get; init; } = 30;

    internal string ResolvedSharedSecret { get; private init; } = string.Empty;

    public static ClientPortalOptions FromConfiguration(IConfiguration configuration, string contentRootPath)
    {
        var configured = configuration.GetSection(SectionName).Get<ClientPortalOptions>() ?? new ClientPortalOptions();
        if (!configured.Enabled)
        {
            return configured;
        }

        if (!Uri.TryCreate(configured.IngestUrl, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("ClientPortal:IngestUrl must be an absolute https:// URL.");
        }

        var secret = configured.SharedSecret;
        if (string.IsNullOrWhiteSpace(secret) && !string.IsNullOrWhiteSpace(configured.SharedSecretFile))
        {
            var path = Path.IsPathRooted(configured.SharedSecretFile)
                ? configured.SharedSecretFile
                : Path.Combine(contentRootPath, configured.SharedSecretFile);
            if (!File.Exists(path))
            {
                throw new InvalidOperationException($"ClientPortal:SharedSecretFile '{path}' does not exist.");
            }

            secret = File.ReadAllText(path);
        }

        secret = secret.Trim();
        if (secret.Length == 0)
        {
            throw new InvalidOperationException("ClientPortal:SharedSecret or ClientPortal:SharedSecretFile is required when enabled.");
        }

        if (configured.PollIntervalSeconds is < 30 or > 86400 || configured.RequestTimeoutSeconds is < 5 or > 300)
        {
            throw new InvalidOperationException("ClientPortal poll interval (30-86400 s) or request timeout (5-300 s) is out of range.");
        }

        return new ClientPortalOptions
        {
            Enabled = true,
            IngestUrl = configured.IngestUrl,
            SharedSecret = string.Empty,
            SharedSecretFile = configured.SharedSecretFile,
            PollIntervalSeconds = configured.PollIntervalSeconds,
            RequestTimeoutSeconds = configured.RequestTimeoutSeconds,
            ResolvedSharedSecret = secret
        };
    }
}
