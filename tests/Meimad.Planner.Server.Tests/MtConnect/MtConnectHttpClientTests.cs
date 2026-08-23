using System.Net;
using System.Text;
using Meimad.Planner.Server.Infrastructure.MtConnect;

namespace Meimad.Planner.Server.Tests.MtConnect;

public sealed class MtConnectHttpClientTests
{
    [Fact]
    public async Task Client_fetches_only_unfiltered_probe_and_current_endpoints()
    {
        var requests = new List<RecordedRequest>();
        using var handler = new StubHandler(request =>
        {
            requests.Add(new(
                request.RequestUri!,
                request.Headers.Accept.Select(value => value.MediaType ?? string.Empty).ToArray()));
            var xml = request.RequestUri!.AbsolutePath == "/probe"
                ? MtConnectTestDocuments.Probe
                : MtConnectTestDocuments.Current;
            return XmlResponse(xml);
        });
        using var http = new HttpClient(handler);
        var client = new MtConnectHttpClient(http);
        var configuredAddress = new Uri("http://192.168.0.56:8082/ignored?path=bad#fragment");

        var probe = await client.ProbeAsync(configuredAddress);
        var current = await client.ReadCurrentAsync(configuredAddress, probe);

        Assert.Equal("VF-3SS", Assert.Single(probe.Devices).Name);
        var device = Assert.Single(current.Devices);
        Assert.Equal("ACTIVE", device.Execution?.Value);
        Assert.Equal(1m, Assert.Single(device.MacroVariables,
            value => value.VariableNumber == 10605).NumericValue);
        Assert.Collection(requests,
            request => AssertRequest(request, "/probe"),
            request => AssertRequest(request, "/current"));
    }

    [Fact]
    public async Task Client_parses_an_HTTP_200_MTConnect_error_as_a_protocol_failure()
    {
        using var handler = new StubHandler(_ => XmlResponse(MtConnectTestDocuments.Error));
        using var http = new HttpClient(handler);
        var client = new MtConnectHttpClient(http);

        var exception = await Assert.ThrowsAsync<MtConnectProtocolException>(() =>
            client.ReadCurrentAsync(new Uri("http://agent:8082")));

        Assert.Contains("INVALID_XPATH", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Client_preserves_non_success_HTTP_status_for_diagnostics()
    {
        using var handler = new StubHandler(_ => new(HttpStatusCode.BadGateway)
        {
            ReasonPhrase = "Upstream unavailable"
        });
        using var http = new HttpClient(handler);
        var client = new MtConnectHttpClient(http);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.ProbeAsync(new Uri("http://agent:8082")));

        Assert.Equal(HttpStatusCode.BadGateway, exception.StatusCode);
        Assert.Contains("MTConnect probe", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("relative/address")]
    [InlineData("ftp://agent:8082")]
    public async Task Client_rejects_invalid_agent_addresses_without_sending(string address)
    {
        var calls = 0;
        using var handler = new StubHandler(_ =>
        {
            calls++;
            return XmlResponse(MtConnectTestDocuments.Probe);
        });
        using var http = new HttpClient(handler);
        var client = new MtConnectHttpClient(http);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.ProbeAsync(new Uri(address, UriKind.RelativeOrAbsolute)));

        Assert.Equal(0, calls);
    }

    private static void AssertRequest(RecordedRequest request, string expectedPath)
    {
        Assert.Equal(expectedPath, request.Uri.AbsolutePath);
        Assert.Equal(string.Empty, request.Uri.Query);
        Assert.Equal(string.Empty, request.Uri.Fragment);
        Assert.Equal(["application/xml", "text/xml"], request.Accept);
    }

    private static HttpResponseMessage XmlResponse(string xml) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(xml, Encoding.UTF8, "text/xml")
    };

    private sealed record RecordedRequest(Uri Uri, IReadOnlyList<string> Accept);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }
}
