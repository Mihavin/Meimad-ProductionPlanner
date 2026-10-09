using System.Net;

namespace Meimad.Planner.Server.Configuration;

internal sealed class LocalAiOptions
{
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "http://127.0.0.1:11434/";
    public string ModelId { get; set; } = "";
    public string ModelDigest { get; set; } = "";
    public string RuntimeVersion { get; set; } = "";
    public int RequestTimeoutSeconds { get; set; } = 60;
    public int MaxQueueWaitSeconds { get; set; } = 30;
    public int MaxInputTokens { get; set; } = 8192;
    public int MaxOutputTokens { get; set; } = 1024;
    public int ForecastTimeoutSeconds { get; set; } = 3;
    /// <summary>CPU threads the model may use per request (Ollama <c>num_thread</c>); 0 leaves the runtime default.</summary>
    public int CpuThreads { get; set; }
    /// <summary>How long the model stays loaded in memory after a request (Ollama <c>keep_alive</c>); 0 unloads at once.</summary>
    public int KeepAliveSeconds { get; set; } = 300;
    /// <summary>The largest model file, in MB, that may be pinned or run; 0 is no limit.</summary>
    public int MaxModelMemoryMb { get; set; }
    /// <summary>The loopback runtime root; the only endpoint the Server will ever talk to.</summary>
    internal Uri BaseUri()
    {
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme != "http"
            || !IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) || !IPAddress.IsLoopback(ip)
            || uri.AbsolutePath != "/" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("Local AI requires an HTTP loopback IP root without credentials, path or query.");
        return uri;
    }

    internal Uri Validate()
    {
        var uri = BaseUri();
        if (string.IsNullOrWhiteSpace(ModelId) || ModelId.Length > 128 || ModelId.Contains(":cloud", StringComparison.OrdinalIgnoreCase)
            || ModelId.Any(char.IsWhiteSpace) || ModelDigest.Length != 64 || !ModelDigest.All(Uri.IsHexDigit)
            || string.IsNullOrWhiteSpace(RuntimeVersion) || RuntimeVersion.Length > 64)
            throw new ArgumentException("Pin an installed local model ID, 64-character SHA-256 digest and tested runtime version before enabling AI.");
        if (RequestTimeoutSeconds is < 1 or > 120 || MaxQueueWaitSeconds is < 1 or > 60
            || MaxInputTokens is < 1024 or > 32768 || MaxOutputTokens is < 128 or > 4096
            || ForecastTimeoutSeconds is < 1 or > 10)
            throw new ArgumentException("Local AI budgets exceed the permitted pilot bounds.");
        if (CpuThreads < 0 || CpuThreads > 256 || KeepAliveSeconds < 0 || KeepAliveSeconds > 86400 || MaxModelMemoryMb < 0 || MaxModelMemoryMb > 4_194_304)
            throw new ArgumentException("CPU threads (0-256), keep-alive seconds (0-86400) and the model memory budget in MB (0 = no limit) are out of range.");
        return uri;
    }
}
