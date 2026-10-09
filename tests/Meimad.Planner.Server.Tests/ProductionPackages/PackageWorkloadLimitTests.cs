using System.Net;
using Meimad.Planner.Server.Application.ProductionPackages;
using Meimad.Planner.Server.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Meimad.Planner.Server.Tests.ProductionPackages.ProductionPackageContextTests.Fixture;

namespace Meimad.Planner.Server.Tests.ProductionPackages;

public sealed class PackageWorkloadLimitTests
{
    [Theory]
    [InlineData("--ProductionPackages:MaximumPackageBytes=1", "production_package_total_size_exceeded")]
    [InlineData("--ProductionPackages:MaximumArtifactBytes=1", "production_package_artifact_size_invalid")]
    public async Task Oversized_input_is_refused_before_activation_and_releases_build_capacity(string setting, string code)
    {
        await using var fixture = await Fixture.CreateAsync(setting);
        var service = fixture.App.Services.GetRequiredService<ProductionPackageService>();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var error = await Assert.ThrowsAsync<ProductionPackageBuildException>(() => service.CreateAsync("operation-package", "test"));
            Assert.Equal(code, error.Code);
        }
        Assert.Equal(0, await fixture.ScalarAsync("SELECT COUNT(*) FROM production_packages"));
        var root = fixture.App.Services.GetRequiredService<ProductionPackageOptions>().ResolvedPackageRoot;
        Assert.Empty(Directory.GetDirectories(root));
    }

    [Fact]
    public async Task Excess_concurrent_build_returns_retryable_response_and_replay_is_still_available()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.App.Services.GetRequiredService<ProductionPackageService>();
        var published = await service.CreateAsync("operation-package", "test", requestId: "done");
        var root = fixture.App.Services.GetRequiredService<ProductionPackageOptions>().ResolvedPackageRoot;
        using (await PackageStorageActivity.BuildAsync(root, default))
        using (await PackageStorageActivity.BuildAsync(root, default))
        {
            using var response = await fixture.Client.PostAsync(Fixture.Path, Fixture.Body());
            Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
            Assert.Equal("1", response.Headers.RetryAfter!.ToString());
            Assert.Equal(published.ProductionPackageId, (await service.CreateAsync("operation-package", "test", requestId: "done")).ProductionPackageId);
        }
    }

    [Fact]
    public void Aggregate_budget_counts_all_sources_and_outputs_independently()
    {
        var budget = new PackageBuildBudget(100);
        budget.AddInput(60); budget.AddInput(40); budget.AddOutput(80);
        Assert.Throws<ProductionPackageBuildException>(() => budget.AddInput(1));
        Assert.Throws<ProductionPackageBuildException>(() => budget.AddOutput(21));
        budget.AddOutput(20);
    }
}
