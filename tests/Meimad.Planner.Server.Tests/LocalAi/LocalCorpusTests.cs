using System.Net;
using System.Net.Http.Json;
using System.Text;
using Meimad.Planner.Server.Application.LocalAi;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Meimad.Planner.Server.Tests.ProductionPackages.ProductionPackageContextTests.Fixture;
namespace Meimad.Planner.Server.Tests.LocalAi;

public sealed class LocalCorpusTests
{
    [Fact]
    public async Task Only_approved_current_text_is_retrieved_and_revocation_clears_the_index()
    {
        await using var fixture = await Fixture.CreateAsync();
        var root = Path.Combine(Path.GetTempPath(), "MeimadCorpus", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var service = fixture.App.Services.GetRequiredService<LocalCorpusService>();
            Assert.Empty((await service.ReadAsync(default)).Roots);
            var file = Path.Combine(root, "instructions.md");
            var text = "RUN-123 שלום ПРИВЕТ. Historical setup reference; verify current context.";
            await File.WriteAllTextAsync(file, text, new UTF8Encoding(false));
            await File.WriteAllTextAsync(Path.Combine(root, "program.nc"), "RUN-123 executable content");
            Directory.CreateDirectory(Path.Combine(root, "child"));
            await File.WriteAllTextAsync(Path.Combine(root, "child", "hidden.txt"), "RUN-123 not approved");
            var settings = await service.UpdateAsync(new(0, [root]), "admin", default);
            var index = await service.IndexAsync(settings.Version, default);
            Assert.Equal(2, index.IndexedFiles);
            foreach (var query in new[] { "RUN-123", "שלום", "привет" })
                Assert.Contains(await service.SearchAsync(query, default), match => match.Title == "instructions.md");
            Assert.Equal("child/hidden.txt", Assert.Single(await service.SearchAsync("not approved", default)).Title);
            var evidence = fixture.App.Services.GetRequiredService<LocalEvidenceService>();
            var bundle = await evidence.ReadAsync(new("FindSimilarIncidents", From: DateTimeOffset.UtcNow.AddHours(-1),
                To: DateTimeOffset.UtcNow, SearchTerm: "RUN-123"), default);
            Assert.True(await evidence.SourcesVisibleAsync(bundle, default));
            Assert.Equal(text, await File.ReadAllTextAsync(file));
            await File.WriteAllTextAsync(file, "Changed source RUN-123");
            Assert.False(await evidence.SourcesVisibleAsync(bundle, default));
            Assert.DoesNotContain(await service.SearchAsync("RUN-123", default), match => match.Title == "instructions.md");
            await service.IndexAsync(settings.Version, default);
            Assert.Contains(await service.SearchAsync("RUN-123", default), match => match.Excerpt.Contains("Changed source", StringComparison.Ordinal));
            File.Delete(file);
            Assert.DoesNotContain(await service.SearchAsync("RUN-123", default), match => match.Title == "instructions.md");
            await service.UpdateAsync(new(settings.Version, []), "admin", default);
            Assert.Equal(0L, await fixture.ScalarAsync("SELECT COUNT(*) FROM local_ai_documents"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateAsync(new(settings.Version, [root]), "admin", default));
            using (fixture.Client.SignedInWithOnly())
            {
                using var denied = await fixture.Client.PutAsJsonAsync("/api/v1/local-ai/corpus", new { expectedVersion = 2, folders = new[] { root } });
                Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
                using var allowed = await fixture.Client.GetAsync("/api/v1/local-ai/documents/search?query=RUN-123");
                allowed.EnsureSuccessStatusCode();
            }
        }
        finally
        {
            Assert.StartsWith(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MeimadCorpus")) + Path.DirectorySeparatorChar, Path.GetFullPath(root));
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Oversized_and_binary_documents_are_isolated_across_approved_subfolders()
    {
        await using var fixture = await Fixture.CreateAsync();
        var root = Path.Combine(Path.GetTempPath(), "MeimadCorpus", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "large.txt"), new string('x', 262145));
            await File.WriteAllBytesAsync(Path.Combine(root, "binary.md"), [0, 1, 255]);
            await File.WriteAllTextAsync(Path.Combine(root, "good.txt"), "Known useful reference");
            var child = Path.Combine(root, "child"); Directory.CreateDirectory(child);
            await File.WriteAllTextAsync(Path.Combine(child, "nested.txt"), "Nested useful reference");
            var service = fixture.App.Services.GetRequiredService<LocalCorpusService>();
            var settings = await service.UpdateAsync(new(0, [root]), "admin", default);
            var result = await service.IndexAsync(settings.Version, default);
            Assert.Equal(2, result.IndexedFiles); Assert.Equal(2, result.SkippedFiles);
        }
        finally
        {
            Assert.StartsWith(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MeimadCorpus")) + Path.DirectorySeparatorChar, Path.GetFullPath(root));
            Directory.Delete(root, true);
        }
    }
}
