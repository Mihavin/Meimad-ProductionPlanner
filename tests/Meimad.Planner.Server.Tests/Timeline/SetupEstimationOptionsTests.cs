using Meimad.Planner.Server.Configuration;
using Microsoft.Extensions.Configuration;

namespace Meimad.Planner.Server.Tests.Timeline;

public sealed class SetupEstimationOptionsTests
{
    [Fact]
    public void Defaults_are_safe_and_can_be_overridden_by_configuration()
    {
        var defaults = SetupEstimationOptions.FromConfiguration(new ConfigurationBuilder().Build());
        Assert.Equal(60, defaults.DefaultToolLoadTimePerToolSeconds);
        Assert.Equal(1.5, defaults.DefaultFirstPieceFactor);

        var configured = SetupEstimationOptions.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SetupEstimation:DefaultToolLoadTimePerToolSeconds"] = "45",
                ["SetupEstimation:DefaultFirstPieceFactor"] = "1.8"
            })
            .Build());
        Assert.Equal(45, configured.DefaultToolLoadTimePerToolSeconds);
        Assert.Equal(1.8, configured.DefaultFirstPieceFactor);
    }

    [Theory]
    [InlineData("-1", "1.5")]
    [InlineData("60", "0.9")]
    public void Invalid_configuration_is_rejected(string loadSeconds, string factor)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SetupEstimation:DefaultToolLoadTimePerToolSeconds"] = loadSeconds,
                ["SetupEstimation:DefaultFirstPieceFactor"] = factor
            })
            .Build();

        Assert.Throws<InvalidOperationException>(
            () => SetupEstimationOptions.FromConfiguration(configuration));
    }
}
