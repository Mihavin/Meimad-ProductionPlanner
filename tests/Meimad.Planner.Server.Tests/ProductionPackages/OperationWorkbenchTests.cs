using System.Net;
using System.Net.Http.Json;
using Meimad.Planner.Server.Application.ProductionPackages;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Meimad.Planner.Server.Tests.ProductionPackages.ProductionPackageContextTests.Fixture;
namespace Meimad.Planner.Server.Tests.ProductionPackages;

public sealed class OperationWorkbenchTests
{
    [Fact]
    public async Task Workbench_reads_explicit_demand_and_recipe_without_mutating_or_accepting_stale_selection()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.ExecuteAsync("""
            INSERT INTO orders(id,case_id,order_reference,quantity,work_finish_date,status)
            VALUES('workbench-order','case-package','ORDER-WB',7,'2026-10-25','open');
            INSERT INTO batch_allocations(id,production_batch_id,allocation_type,order_id,quantity) VALUES
              ('wb-order','batch-package','order','workbench-order',7),
              ('wb-stock','batch-package','stock',NULL,2),
              ('wb-scrap','batch-package','scrap_allowance',NULL,1);
            """);
        var service = fixture.App.Services.GetRequiredService<ExecutionContextService>();
        var exact = Assert.Single(await service.ListAsync("operation-package", default));
        var result = await service.ReadWorkbenchAsync("operation-package", exact.Selection, default);
        Assert.Equal("case-package", result!.CaseId);
        Assert.Equal("batch-package", result.BatchId);
        Assert.Equal("case-operation-package", result.RecipeOperationId);
        Assert.Equal(exact.ProcessRevisionId, result.ProcessRevisionId);
        Assert.False(result.AllocationsTruncated);
        Assert.Equal("ORDER-WB", Assert.Single(result.Allocations, x => x.Type == "order").OrderReference);
        Assert.Equal(await fixture.ScalarAsync("SELECT SUM(quantity) FROM batch_allocations WHERE production_batch_id='batch-package'"), result.Allocations.Sum(x => x.Quantity));
        await fixture.ExecuteAsync("UPDATE machine_assignments SET version=version+1 WHERE id='assignment-package'");
        await Assert.ThrowsAsync<ProductionPackageBuildException>(() => service.ReadWorkbenchAsync("operation-package", exact.Selection, default));
        using (fixture.Client.SignedInWithOnly())
        {
            using var allowed = await fixture.Client.GetAsync("/api/v1/batch-operations/operation-package/workbench");
            allowed.EnsureSuccessStatusCode();
            Assert.Null((await allowed.Content.ReadFromJsonAsync<OperationWorkbenchDetails>())!.ProcessRevisionId);
        }
        using (fixture.Client.SignedOut())
        {
            using var denied = await fixture.Client.GetAsync("/api/v1/batch-operations/operation-package/workbench");
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }
        Assert.Equal(0L, await fixture.ScalarAsync("SELECT COUNT(*) FROM production_packages"));
    }
}
