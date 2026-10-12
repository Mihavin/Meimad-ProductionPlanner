using System.Net;
using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Presentation;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

/// <summary>The TV picture card of the selected Case Operation (owner request 2026-10-12, Server schema v118).</summary>
public sealed class OperationThumbnailViewModelTests
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    [Fact]
    public async Task Card_says_which_picture_the_TV_shows_and_stores_a_snapshot_with_the_loaded_version()
    {
        var api = new FakeApi();
        api.Items.Add(Item("package", 1));
        var viewModel = new OperationThumbnailViewModel();
        viewModel.AttachSession(api, "windows-01", editor: true);

        await viewModel.LoadAsync("case-1", "op-10");

        Assert.Equal("The TV shows the package thumbnail.", viewModel.TvSummary);
        Assert.Equal("Package thumbnail: package.png (1 KB)", viewModel.PackageText);
        Assert.Equal("Snapshot: none", viewModel.SnapshotText);
        Assert.NotNull(viewModel.Preview);
        Assert.True(viewModel.RemovePackageCommand.CanExecute(null));
        Assert.False(viewModel.RemoveSnapshotCommand.CanExecute(null));

        Assert.True(await viewModel.UploadSnapshotAsync(Png, "view.png", @"C:\shots\view.png"));
        var upload = Assert.Single(api.Uploads);
        Assert.Equal(("case-1", "op-10", "snapshot", "view.png", (string?)null, @"C:\shots\view.png"), upload);
        Assert.Equal("The snapshot is stored on the Server for the TV Dashboard.", viewModel.StatusMessage);
        Assert.Equal("Snapshot: view.png (1 KB)", viewModel.SnapshotText);

        // The next replace carries the version the card loaded.
        Assert.True(await viewModel.UploadSnapshotAsync(Png, "again.png", null));
        Assert.Equal("\"case-operation-thumbnail:op-10:snapshot:v1\"", api.Uploads[1].EntityTag);
    }

    [Fact]
    public async Task Case_picture_becomes_the_snapshot_and_removal_and_errors_leave_a_message()
    {
        var api = new FakeApi { TvSource = "case_picture", CasePicture = Png };
        api.Items.Add(Item("snapshot", 3));
        var viewModel = new OperationThumbnailViewModel();
        viewModel.AttachSession(api, "windows-01", editor: true);
        await viewModel.LoadAsync("case-1", "op-10");
        Assert.Equal("The TV shows the Case picture (it comes before the Operation thumbnails).", viewModel.TvSummary);

        viewModel.UseCasePictureCommand.Execute(null);
        for (var attempt = 0; attempt < 100 && api.Uploads.Count == 0; attempt++) await Task.Delay(10);
        Assert.Equal(("case-1", "op-10", "snapshot", "case-picture.png", "\"case-operation-thumbnail:op-10:snapshot:v3\"", "Case picture"), Assert.Single(api.Uploads));

        api.FailDelete = true;
        viewModel.RemoveSnapshotCommand.Execute(null);
        for (var attempt = 0; attempt < 100 && api.Deletes == 0; attempt++) await Task.Delay(10);
        for (var attempt = 0; attempt < 100 && viewModel.IsBusy; attempt++) await Task.Delay(10);
        Assert.Equal("The Operation thumbnail changed since you opened it, so your change was not saved.", viewModel.StatusMessage);

        // A user without edit rights only sees the card.
        viewModel.AttachSession(api, "windows-01", editor: false);
        Assert.False(viewModel.CanEdit);
        Assert.False(viewModel.UseCasePictureCommand.CanExecute(null));
        Assert.False(await viewModel.UploadSnapshotAsync(Png, "x.png", null));

        await viewModel.LoadAsync(null, null);
        Assert.Equal("Select an Operation to see its TV picture.", viewModel.TvSummary);
    }

    private static OperationThumbnailInfo Item(string source, int version) => new(
        "op-10", source, source + ".png", "image/png", Png.Length, "aa", null, version,
        $"\"case-operation-thumbnail:op-10:{source}:v{version}\"", DateTimeOffset.UtcNow, "planner");

    private sealed class FakeApi : StubPlannerApiClient, IPlannerApiClient
    {
        internal List<OperationThumbnailInfo> Items { get; } = [];
        internal List<(string CaseId, string OperationId, string Source, string FileName, string? EntityTag, string? Reference)> Uploads { get; } = [];
        internal string? TvSource { get; init; }
        internal byte[]? CasePicture { get; init; }
        internal bool FailDelete { get; set; }
        internal int Deletes;

        public Task<OperationThumbnailList> ListOperationThumbnailsAsync(string caseId, string caseOperationId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new OperationThumbnailList(caseOperationId,
                TvSource ?? Items.Select(item => item.Source).OrderBy(source => source == "package" ? 0 : 1).FirstOrDefault() ?? "placeholder",
                Items.ToArray()));

        public Task<byte[]?> GetOperationThumbnailImageAsync(string caseId, string caseOperationId, string source, CancellationToken cancellationToken = default) =>
            Task.FromResult<byte[]?>(Png);

        public override Task<byte[]?> GetCasePreviewAsync(string caseId, CancellationToken cancellationToken = default) =>
            Task.FromResult(CasePicture);

        public Task<OperationThumbnailInfo> UploadOperationThumbnailAsync(
            string caseId, string caseOperationId, string source, string fileName, byte[] bytes, string? entityTag, string clientId,
            string? sourceReference = null, CancellationToken cancellationToken = default)
        {
            Uploads.Add((caseId, caseOperationId, source, fileName, entityTag, sourceReference));
            var current = Items.FirstOrDefault(item => item.Source == source);
            var saved = Item(source, (current?.Version ?? 0) + 1) with { FileName = fileName };
            if (current is not null) Items.Remove(current);
            Items.Add(saved);
            return Task.FromResult(saved);
        }

        public Task DeleteOperationThumbnailAsync(string caseId, string caseOperationId, string source, string entityTag, string clientId, CancellationToken cancellationToken = default)
        {
            Deletes++;
            if (FailDelete)
                throw new PlannerApiException(HttpStatusCode.PreconditionFailed, "resource_version_stale",
                    "The Operation thumbnail changed since you opened it, so your change was not saved.");
            Items.RemoveAll(item => item.Source == source);
            return Task.CompletedTask;
        }
    }
}
