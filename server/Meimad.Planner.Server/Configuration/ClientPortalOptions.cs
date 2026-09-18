namespace Meimad.Planner.Server.Configuration;

/// <summary>
/// Push of customer-safe Order status to the cloud customer portal (the separate
/// <c>meimad-client-cloud-portal</c> project). Disabled by default: nothing leaves
/// the Server unless <c>ClientPortal:Enabled</c> is true and at least one customer
/// is mapped. The Server needs no Google credential for this; it only holds the
/// portal's ingest shared secret, which can call one ingest endpoint and nothing else.
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

    /// <summary>Which customers are pushed, and under which portal customer id.</summary>
    public ClientPortalCustomer[] Customers { get; init; } = [];

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

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var customer in configured.Customers)
        {
            if (string.IsNullOrWhiteSpace(customer.Customer) || string.IsNullOrWhiteSpace(customer.CustomerId))
            {
                throw new InvalidOperationException("Every ClientPortal:Customers entry needs both Customer and CustomerId.");
            }

            if (!ClientPortalCustomer.IsValidCustomerId(customer.CustomerId))
            {
                throw new InvalidOperationException(
                    $"ClientPortal customer id '{customer.CustomerId}' must be 1-64 lowercase letters, digits, '-' or '_'.");
            }

            if (!seen.Add(customer.CustomerId))
            {
                throw new InvalidOperationException($"ClientPortal customer id '{customer.CustomerId}' is listed twice.");
            }
        }

        return new ClientPortalOptions
        {
            Enabled = true,
            IngestUrl = configured.IngestUrl,
            SharedSecret = string.Empty,
            SharedSecretFile = configured.SharedSecretFile,
            PollIntervalSeconds = configured.PollIntervalSeconds,
            RequestTimeoutSeconds = configured.RequestTimeoutSeconds,
            Customers = configured.Customers.Select(c => new ClientPortalCustomer
            {
                Customer = c.Customer.Trim(),
                CustomerId = c.CustomerId.Trim()
            }).ToArray(),
            ResolvedSharedSecret = secret
        };
    }
}

public sealed class ClientPortalCustomer
{
    /// <summary>The exact Case.customer value (matched case-insensitively, otherwise exactly).</summary>
    public string Customer { get; init; } = string.Empty;

    /// <summary>The portal's customer id (Firestore document id and Firebase Auth claim).</summary>
    public string CustomerId { get; init; } = string.Empty;

    internal static bool IsValidCustomerId(string value) =>
        value.Length is >= 1 and <= 64 && value.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '_');
}
