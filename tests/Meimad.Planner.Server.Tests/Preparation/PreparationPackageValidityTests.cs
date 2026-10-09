using Meimad.Planner.Server.Application.ProductionPackages;
using Meimad.Planner.Server.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Meimad.Planner.Server.Tests.ProductionPackages.ProductionPackageContextTests.Fixture;

namespace Meimad.Planner.Server.Tests.Preparation;

public sealed class PreparationPackageValidityTests
{
    [Theory]
    [InlineData("")]
    [InlineData("UPDATE cnc_verification_settings SET version=version+1")]
    [InlineData("UPDATE cnc_verification_settings SET enabled=0")]
    [InlineData("UPDATE machines SET execution_mode='MANUAL'")]
    [InlineData("UPDATE process_revisions SET is_active=0")]
    [InlineData("UPDATE production_run_outputs SET target_quantity=9")]
    [InlineData("INSERT INTO tool_preparations(id,batch_operation_id,machine_id,tool_table_release_id,version_number,saved_at,saved_by,content_hash) VALUES('new-measurements','operation-package','machine-package','tools-1',1,'2026-10-08T10:00:00Z','tool-room','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa')")]
    public async Task Batched_queue_validity_matches_full_current_package_read(string change)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.App.Services.GetRequiredService<ProductionPackageService>().CreateAsync("operation-package", "test");
        if (change.Length > 0) await fixture.ExecuteAsync(change);
        await CompareAsync(fixture);
    }

    [Fact]
    public async Task Split_outputs_and_missing_packages_match_full_read()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.App.Services.GetRequiredService<ProductionPackageService>().CreateAsync("operation-package", "test");
        await fixture.SplitAsync();
        await CompareAsync(fixture);
    }

    private static async Task CompareAsync(Fixture fixture)
    {
        var queue = await new SqlitePreparationQueueRepository(fixture.Database).ReadSourcesAsync(default);
        Assert.NotEmpty(queue);
        await using var connection = await fixture.Database.OpenConnectionAsync();
        await using var transaction = connection.BeginTransaction(deferred: true);
        foreach (var row in queue)
        {
            var full = await SqliteProductionPackageRepository.ReadCurrentAsync(connection, transaction, row.Context!, default);
            Assert.Equal(full is not null, row.HasCurrentValidProductionPackage);
        }
    }
}
