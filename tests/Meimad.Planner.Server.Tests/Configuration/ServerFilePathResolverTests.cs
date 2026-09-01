using System.Net;
using Meimad.Planner.Server.Configuration;
using Microsoft.Extensions.Configuration;

namespace Meimad.Planner.Server.Tests.ServerConfiguration;

public sealed class ServerFilePathResolverTests
{
    [Fact]
    public void Resolver_tries_each_ipv4_address_when_a_mapped_unc_host_is_multihomed()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FileAccess:DriveMappings:0:Drive"] = "J:",
            ["FileAccess:DriveMappings:0:NetworkPath"] = @"\\file-server\DATA"
        }).Build();
        var attempted = new List<string>();
        const string reachable = @"\\192.168.0.240\DATA\customers\part-preview.png";
        var resolver = new ServerFilePathResolver(
            ServerFileAccessOptions.FromConfiguration(configuration),
            _ => [IPAddress.Parse("172.16.56.2"), IPAddress.Parse("192.168.0.240")],
            candidate =>
            {
                attempted.Add(candidate);
                return string.Equals(candidate, reachable, StringComparison.OrdinalIgnoreCase);
            });

        var resolved = resolver.ResolveExistingFile(
            @"J:\customers\part-preview.png",
            @"J:\customers");

        Assert.Equal(reachable, resolved);
        Assert.Contains(@"\\file-server\DATA\customers\part-preview.png", attempted);
        Assert.Contains(@"\\172.16.56.2\DATA\customers\part-preview.png", attempted);
        Assert.Contains(reachable, attempted);
    }
}
