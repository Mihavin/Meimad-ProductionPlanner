using System.Net.Http.Headers;

namespace Meimad.Planner.Server.Infrastructure.MtConnect;

internal interface IMtConnectClient
{
    Task<MtConnectProbeDocument> ProbeAsync(
        Uri agentBaseAddress,
        CancellationToken cancellationToken = default);

    Task<MtConnectCurrentDocument> ReadCurrentAsync(
        Uri agentBaseAddress,
        CancellationToken cancellationToken = default);

    Task<MtConnectCurrentDocument> ReadCurrentAsync(
        Uri agentBaseAddress,
        MtConnectProbeDocument probe,
        CancellationToken cancellationToken = default);
}

internal sealed class MtConnectHttpClient(HttpClient httpClient) : IMtConnectClient
{
    public Task<MtConnectProbeDocument> ProbeAsync(
        Uri agentBaseAddress,
        CancellationToken cancellationToken = default) =>
        GetAsync(agentBaseAddress, "probe", MtConnectDocumentParser.ParseProbeAsync, cancellationToken);

    public Task<MtConnectCurrentDocument> ReadCurrentAsync(
        Uri agentBaseAddress,
        CancellationToken cancellationToken = default) =>
        GetAsync(agentBaseAddress, "current",
            (stream, token) => MtConnectDocumentParser.ParseCurrentAsync(stream, null, token),
            cancellationToken);

    public Task<MtConnectCurrentDocument> ReadCurrentAsync(
        Uri agentBaseAddress,
        MtConnectProbeDocument probe,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(probe);
        return GetAsync(agentBaseAddress, "current",
            (stream, token) => MtConnectDocumentParser.ParseCurrentAsync(stream, probe, token),
            cancellationToken);
    }

    private async Task<T> GetAsync<T>(
        Uri agentBaseAddress,
        string endpoint,
        Func<Stream, CancellationToken, Task<T>> parse,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildEndpoint(agentBaseAddress, endpoint));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/xml"));
        using var response = await httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"MTConnect {endpoint} returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).",
                null,
                response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await parse(stream, cancellationToken);
    }

    private static Uri BuildEndpoint(Uri agentBaseAddress, string endpoint)
    {
        ArgumentNullException.ThrowIfNull(agentBaseAddress);
        if (!agentBaseAddress.IsAbsoluteUri)
            throw new ArgumentException("The MTConnect agent address must be absolute.", nameof(agentBaseAddress));
        if (agentBaseAddress.Scheme is not ("http" or "https"))
            throw new ArgumentException("The MTConnect agent address must use HTTP or HTTPS.", nameof(agentBaseAddress));

        var builder = new UriBuilder(agentBaseAddress)
        {
            Path = $"/{endpoint}",
            Query = string.Empty,
            Fragment = string.Empty
        };
        return builder.Uri;
    }
}
