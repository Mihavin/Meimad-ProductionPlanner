using System.Net;
using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Presentation;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

public sealed class KitaronPushViewModelTests
{
    [Fact]
    public async Task Refused_push_retains_preview_precondition_until_the_user_refreshes_it()
    {
        var api = new FakeApiClient { RefusePush = true };
        var viewModel = new KitaronPushViewModel();
        viewModel.AttachSession(api, editor: true);
        await viewModel.PreviewAsync();
        await viewModel.PushNowAsync();
        Assert.Equal("preview-1", api.LastPreviewStamp);
        viewModel.AttachSession(api, editor: true);
        await viewModel.PushNowAsync();
        Assert.Equal("preview-1", api.LastPreviewStamp);
        viewModel.AttachSession(null, editor: false);
        viewModel.AttachSession(api, editor: true);
        await viewModel.PushNowAsync();
        Assert.Null(api.LastPreviewStamp);
    }

    [Fact]
    public async Task Each_pushable_column_offers_only_values_of_its_kind_and_saving_sends_every_choice()
    {
        var api = new FakeApiClient();
        var viewModel = new KitaronPushViewModel();
        viewModel.AttachSession(api, editor: true);
        await viewModel.RefreshAsync();

        Assert.Equal(["OperationQty", "StartDateReal"], viewModel.Columns.Select(row => row.ColumnName));
        var quantity = viewModel.Columns[0];
        Assert.Equal(["good_quantity", "planned_quantity"], quantity.Choices.Select(choice => choice.Code));
        Assert.Equal("good_quantity", quantity.SelectedValue!.Code);
        Assert.True(quantity.Push);
        var start = viewModel.Columns[1];
        Assert.Equal("actual_start", start.SelectedValue!.Code);   // not configured: first date value, not pushed
        Assert.False(start.Push);

        quantity.SelectedValue = quantity.Choices[1];
        start.Push = true;
        viewModel.Enabled = true;
        viewModel.IntervalMinutes = "30";
        await viewModel.SaveAsync();

        Assert.Equal((true, 30, 3), (api.SavedEnabled, api.SavedInterval, api.SavedVersion));
        Assert.Equal(
            [new KitaronPushMappingModel("OperationQty", "planned_quantity", true), new KitaronPushMappingModel("StartDateReal", "actual_start", true)],
            api.SavedMappings);
        Assert.Contains("every 30 minutes", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preview_lists_the_changes_without_writing_and_push_now_logs_the_run()
    {
        var api = new FakeApiClient();
        var viewModel = new KitaronPushViewModel();
        viewModel.AttachSession(api, editor: true);
        await viewModel.RefreshAsync();

        await viewModel.PreviewAsync();
        Assert.Equal(0, api.Pushes);
        Assert.Equal("41043", Assert.Single(viewModel.Changes).WorkOrderNumber.ToString());
        Assert.Contains("Nothing was written", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Equal("1 operations have no Kitaron operation.", Assert.Single(viewModel.Notes));

        await viewModel.PushNowAsync();
        Assert.Equal(1, api.Pushes);
        Assert.Equal("preview-1", api.LastPreviewStamp);
        Assert.Equal("1 values written to Kitaron.", viewModel.StatusMessage);
        Assert.Single(viewModel.Runs);
    }

    [Fact]
    public async Task A_refused_push_explains_why_and_the_setup_is_read_only_without_permission()
    {
        var api = new FakeApiClient { RefusePush = true };
        var viewModel = new KitaronPushViewModel();
        viewModel.AttachSession(api, editor: false);
        await viewModel.RefreshAsync();

        Assert.False(viewModel.CanEdit);
        Assert.False(viewModel.PushNowCommand.CanExecute(null));

        viewModel.AttachSession(api, editor: true);
        await viewModel.PushNowAsync();
        Assert.Equal("The Kitaron connector is switched off on the Server's Kitaron setup page.", viewModel.StatusMessage);
    }

    private sealed class FakeApiClient : StubPlannerApiClient, IPlannerApiClient
    {
        private readonly List<KitaronPushRunInfo> runs = [];

        internal bool RefusePush { get; init; }
        internal bool SavedEnabled { get; private set; }
        internal int SavedInterval { get; private set; }
        internal int SavedVersion { get; private set; }
        internal IReadOnlyList<KitaronPushMappingModel> SavedMappings { get; private set; } = [];
        internal int Pushes { get; private set; }
        internal string? LastPreviewStamp { get; private set; }

        public Task<KitaronPushSettingsResource> GetKitaronPushAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Settings());

        public Task<KitaronPushSettingsResource> UpdateKitaronPushAsync(
            bool enabled, int intervalMinutes, IReadOnlyList<KitaronPushMappingModel> mappings, int expectedVersion,
            CancellationToken cancellationToken = default)
        {
            (SavedEnabled, SavedInterval, SavedVersion, SavedMappings) = (enabled, intervalMinutes, expectedVersion, mappings);
            return Task.FromResult(Settings() with { Enabled = enabled, IntervalMinutes = intervalMinutes, Mappings = mappings, Version = 4 });
        }

        public Task<KitaronPushResultInfo> PreviewKitaronPushAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Result(null));

        public Task<KitaronPushResultInfo> RunKitaronPushAsync(CancellationToken cancellationToken = default, string? previewStamp = null)
        {
            LastPreviewStamp = previewStamp;
            if (RefusePush)
            {
                throw new PlannerApiException(HttpStatusCode.Conflict, "kitaron_push_blocked",
                    "The Kitaron connector is switched off on the Server's Kitaron setup page.");
            }
            Pushes++;
            runs.Add(new KitaronPushRunInfo("run-1", "manual", "admin", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                "succeeded", 1, 1, 1, "1 values written."));
            return Task.FromResult(Result("run-1"));
        }

        private KitaronPushSettingsResource Settings() => new(
            false, 15, [new KitaronPushMappingModel("OperationQty", "good_quantity", true)], 3, DateTimeOffset.UtcNow, "admin",
            [
                new KitaronPushColumnInfo("OperationQty", "number", "Operation quantity", "Empty in Kitaron."),
                new KitaronPushColumnInfo("StartDateReal", "date", "Real start date", "Kitaron also sets it.")
            ],
            [
                new KitaronPushValueInfo("actual_start", "date", "Actual start", ""),
                new KitaronPushValueInfo("forecast_finish", "date", "Forecast finish (Timeline)", ""),
                new KitaronPushValueInfo("good_quantity", "number", "Good quantity made", ""),
                new KitaronPushValueInfo("planned_quantity", "number", "Planned quantity", "")
            ],
            runs.ToArray());

        private static KitaronPushResultInfo Result(string? runId) => new(
            runId, runId is not null, DateTimeOffset.UtcNow, 1, 1,
            [new KitaronPushChangeInfo(41043, "10", 5001, "PN-1", "Mill", "OperationQty", null, "6")],
            ["1 operations have no Kitaron operation."], "preview-1");
    }
}
