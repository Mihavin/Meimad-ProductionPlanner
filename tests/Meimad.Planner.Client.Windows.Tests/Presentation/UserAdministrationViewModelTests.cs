using System.Net;
using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Presentation;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

public sealed class UserAdministrationViewModelTests
{
    [Fact]
    public async Task Administrator_creates_a_user_with_types_and_edits_a_types_permissions()
    {
        var api = new FakeApiClient();
        var viewModel = new UserAdministrationViewModel();
        viewModel.AttachSession(api, "admin");
        await viewModel.RefreshAsync();

        Assert.Equal(["Administrator", "QC"], viewModel.UserTypes.Select(type => type.Name));
        Assert.Single(viewModel.Users);

        viewModel.NewUserCommand.Execute(null);
        viewModel.UserName = "dana";
        viewModel.DisplayName = "Dana Cohen";
        viewModel.TemporaryPassword = "temporary-1";
        viewModel.UserTypeChoices.Single(choice => choice.Name == "QC").IsSelected = true;
        await Execute(viewModel.SaveUserCommand);

        Assert.Equal(("dana", "Dana Cohen", "temporary-1"), api.Created);
        Assert.Equal(["user-type-qc"], api.CreatedTypes);
        Assert.Contains("temporary password", viewModel.Status, StringComparison.Ordinal);

        viewModel.SelectedType = viewModel.UserTypes.Single(type => type.IsAdministrator);
        Assert.False(viewModel.IsTypeEditable);
        Assert.All(viewModel.PermissionChoices, choice => Assert.True(choice.IsSelected && !choice.IsEnabled));
        Assert.False(viewModel.DeleteTypeCommand.CanExecute(null));

        viewModel.SelectedType = viewModel.UserTypes.Single(type => type.Name == "QC");
        viewModel.PermissionChoices.Single(choice => choice.Key == PlannerPermissions.PrepareTools).IsSelected = true;
        await Execute(viewModel.SaveTypeCommand);

        Assert.Equal(("user-type-qc", 4), api.UpdatedType);
        Assert.Equal([PlannerPermissions.DecideQc, PlannerPermissions.PrepareTools], api.UpdatedPermissions);
    }

    [Fact]
    public async Task A_refused_stale_edit_shows_the_servers_explanation_and_advice()
    {
        var api = new FakeApiClient { RefuseTypeUpdate = true };
        var viewModel = new UserAdministrationViewModel();
        viewModel.AttachSession(api, "admin");
        await viewModel.RefreshAsync();

        viewModel.SelectedType = viewModel.UserTypes.Single(type => type.Name == "QC");
        await Execute(viewModel.SaveTypeCommand);

        Assert.Contains("was changed by Rina", viewModel.Status, StringComparison.Ordinal);
        Assert.Contains("Refresh the user types", viewModel.Status, StringComparison.Ordinal);
    }

    private static async Task Execute(AsyncCommand command)
    {
        command.Execute(null);
        // AsyncCommand runs the task from Execute; wait until the command is free again.
        for (var attempt = 0; attempt < 100 && !command.CanExecute(null); attempt++) await Task.Delay(10);
    }

    private sealed class FakeApiClient : StubPlannerApiClient, IPlannerApiClient
    {
        private readonly List<UserAccountInfo> users =
        [
            new("user-admin", "admin", "Owner", true, false, null,
                [new UserTypeSummary("user-type-administrator", "Administrator", true)], 1, DateTimeOffset.UtcNow, null)
        ];

        internal bool RefuseTypeUpdate { get; init; }
        internal (string UserName, string DisplayName, string Password)? Created { get; private set; }
        internal IReadOnlyList<string> CreatedTypes { get; private set; } = [];
        internal (string UserTypeId, int Version)? UpdatedType { get; private set; }
        internal IReadOnlyList<string> UpdatedPermissions { get; private set; } = [];

        public Task<IReadOnlyList<PermissionInfo>> ListPermissionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PermissionInfo>>(
            [
                new(PlannerPermissions.DecideQc, "Decide QC", "Accept or reject Production Runs."),
                new(PlannerPermissions.PrepareTools, "Prepare tools", "Work the Tool Room queue.")
            ]);

        public Task<IReadOnlyList<UserTypeInfo>> ListUserTypesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<UserTypeInfo>>(
            [
                new("user-type-qc", "QC", "Manages the QC queue.", false, [PlannerPermissions.DecideQc], 0, 4, DateTimeOffset.UtcNow, null),
                new("user-type-administrator", "Administrator", null, true, [], 1, 1, DateTimeOffset.UtcNow, null)
            ]);

        public Task<IReadOnlyList<UserAccountInfo>> ListUsersAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<UserAccountInfo>>(users.ToArray());

        public Task<UserAccountInfo> CreateUserAsync(
            string userName, string displayName, string password, bool isActive, IReadOnlyList<string> userTypeIds,
            CancellationToken cancellationToken = default)
        {
            Created = (userName, displayName, password);
            CreatedTypes = userTypeIds;
            var created = new UserAccountInfo("user-dana", userName, displayName, isActive, true, null,
                [new UserTypeSummary("user-type-qc", "QC", false)], 1, DateTimeOffset.UtcNow, "admin");
            users.Add(created);
            return Task.FromResult(created);
        }

        public Task<UserTypeInfo> UpdateUserTypeAsync(
            string userTypeId, string name, string? description, IReadOnlyList<string> permissions, int expectedVersion,
            CancellationToken cancellationToken = default)
        {
            if (RefuseTypeUpdate)
            {
                throw new PlannerApiException(
                    HttpStatusCode.Conflict, "edit_conflict", "The user type 'QC' was changed by Rina after you opened it.",
                    conflict: new PlannerConflict("user type", "Rina", DateTimeOffset.UtcNow,
                        "Refresh the user types to see its current permissions, then apply your change again."));
            }

            UpdatedType = (userTypeId, expectedVersion);
            UpdatedPermissions = permissions;
            return Task.FromResult(new UserTypeInfo(userTypeId, name, description, false, permissions, 0, expectedVersion + 1, DateTimeOffset.UtcNow, "admin"));
        }
    }
}
