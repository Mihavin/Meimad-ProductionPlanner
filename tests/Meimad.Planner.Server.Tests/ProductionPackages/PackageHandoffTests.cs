using System.Net;
using System.Net.Http.Json;
using Meimad.Planner.Server.Application.ProductionPackages;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Meimad.Planner.Server.Tests.ProductionPackages.ProductionPackageContextTests.Fixture;
namespace Meimad.Planner.Server.Tests.ProductionPackages;

public sealed class PackageHandoffTests
{
    [Fact]
    public async Task Handoff_is_exact_idempotent_tracking_and_survives_as_history_after_package_supersession()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.ExecuteAsync("""
            INSERT INTO employee_resources(id,employee_number,name,resource_type,skills_json) VALUES
                ('setup-worker','E1','Qualified setup','setup_worker','["machine-package"]'),
                ('unqualified','E2','Other worker','setup_worker','["other-machine"]');
            """);
        var packages = fixture.App.Services.GetRequiredService<ProductionPackageService>();
        var package = await packages.CreateAsync("operation-package", "test");
        var service = fixture.App.Services.GetRequiredService<PackageHandoffService>();
        var view = await service.ReadAsync(package.ProductionPackageId, default);
        Assert.True(view.PackageIsCurrent); Assert.False(view.HandoffIsCurrent);
        var worker = Assert.Single(view.QualifiedWorkers);
        var context = Assert.Single(await fixture.App.Services.GetRequiredService<ExecutionContextService>().ListAsync("operation-package", default));
        var request = new HandoffRequest("request", worker.EmployeeId, worker.QualificationStamp, view.Version, context);
        using (fixture.Client.SignedInWithOnly())
        {
            using var denied = await fixture.Client.PostAsJsonAsync("/api/v1/production-packages/" + package.ProductionPackageId + "/handoffs", request);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        var first = await service.RecordAsync(package.ProductionPackageId, request, "toolroom", default);
        Assert.Equal(first, await service.RecordAsync(package.ProductionPackageId, request, "toolroom", default));
        Assert.True((await service.ReadAsync(package.ProductionPackageId, default)).HandoffIsCurrent);
        Assert.Equal(1L, await fixture.ScalarAsync("SELECT COUNT(*) FROM production_package_handoffs"));
        Assert.Equal(1L, await fixture.ScalarAsync("SELECT COUNT(*) FROM structured_event_log WHERE event_type='physical_package_handoff_recorded'"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RecordAsync(package.ProductionPackageId, request with { RequestId = "competing" }, "toolroom", default));
        var replacement = await packages.CreateAsync("operation-package", "test", requestId: "replacement");
        var old = await service.ReadAsync(package.ProductionPackageId, default);
        Assert.False(old.PackageIsCurrent); Assert.False(old.HandoffIsCurrent); Assert.Single(old.History);
        Assert.False((await service.ReadAsync(replacement.ProductionPackageId, default)).HandoffIsCurrent);
        Assert.Equal(first, await service.RecordAsync(package.ProductionPackageId, request, "toolroom", default));
        Assert.Equal(1L, await fixture.ScalarAsync("SELECT COUNT(*) FROM production_runs WHERE status='PLANNED'"));
    }

    [Fact]
    public async Task Qualification_and_context_changes_refuse_new_handoffs_and_invalidate_current_tracking()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.ExecuteAsync("INSERT INTO employee_resources(id,employee_number,name,resource_type,skills_json) VALUES('setup-worker','E1','Setup','setup_worker','[\"*\"]')");
        var package = await fixture.App.Services.GetRequiredService<ProductionPackageService>().CreateAsync("operation-package", "test");
        var service = fixture.App.Services.GetRequiredService<PackageHandoffService>();
        var worker = Assert.Single((await service.ReadAsync(package.ProductionPackageId, default)).QualifiedWorkers);
        var context = Assert.Single(await fixture.App.Services.GetRequiredService<ExecutionContextService>().ListAsync("operation-package", default));
        var request = new HandoffRequest("first", worker.EmployeeId, worker.QualificationStamp, 0, context);
        await service.RecordAsync(package.ProductionPackageId, request, "toolroom", default);
        await fixture.ExecuteAsync("UPDATE employee_resources SET skills_json='[]',version=version+1 WHERE id='setup-worker'");
        Assert.False((await service.ReadAsync(package.ProductionPackageId, default)).HandoffIsCurrent);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RecordAsync(package.ProductionPackageId, request with { RequestId = "second", ExpectedVersion = 1 }, "toolroom", default));
        await fixture.ExecuteAsync("UPDATE machine_assignments SET version=version+1 WHERE id='assignment-package'");
        await Assert.ThrowsAsync<ProductionPackageBuildException>(() => service.RecordAsync(package.ProductionPackageId, request with { RequestId = "third", ExpectedVersion = 1 }, "toolroom", default));
        Assert.Equal(1L, await fixture.ScalarAsync("SELECT COUNT(*) FROM production_package_handoffs"));
    }
}
