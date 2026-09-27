using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Net.Http;
using System.Runtime.CompilerServices;
using Meimad.Planner.Client.Windows.Api;

namespace Meimad.Planner.Client.Windows.Presentation;

/// <summary>
/// The administrator's page: user accounts, user types and the permissions each type grants. The
/// Server owns every rule (unique names, the fixed Administrator type, the last administrator) and
/// refuses an edit based on an older version with a conflict that the main window explains.
/// </summary>
internal sealed class UserAdministrationViewModel : INotifyPropertyChanged
{
    private IPlannerApiClient? api;
    private string actor = string.Empty;
    private bool isBusy;
    private string status = "Only administrators see this page.";
    private UserRow? selectedUser;
    private bool isNewUser;
    private string userName = string.Empty;
    private string displayName = string.Empty;
    private bool userIsActive = true;
    private string temporaryPassword = string.Empty;
    private UserTypeInfo? selectedType;
    private bool isNewType;
    private string typeName = string.Empty;
    private string typeDescription = string.Empty;
    private IReadOnlyList<PermissionInfo> permissions = [];

    internal UserAdministrationViewModel()
    {
        RefreshCommand = new AsyncCommand(RefreshAsync, () => api is not null && !isBusy);
        NewUserCommand = new AsyncCommand(() => { BeginNewUser(); return Task.CompletedTask; }, () => api is not null && !isBusy);
        SaveUserCommand = new AsyncCommand(SaveUserAsync, () => api is not null && !isBusy && (isNewUser || selectedUser is not null));
        ResetPasswordCommand = new AsyncCommand(ResetPasswordAsync, () => api is not null && !isBusy && !isNewUser && selectedUser is not null);
        NewTypeCommand = new AsyncCommand(() => { BeginNewType(); return Task.CompletedTask; }, () => api is not null && !isBusy);
        SaveTypeCommand = new AsyncCommand(SaveTypeAsync, () => api is not null && !isBusy && IsTypeEditable);
        DeleteTypeCommand = new AsyncCommand(DeleteTypeAsync,
            () => api is not null && !isBusy && !isNewType && selectedType is { IsAdministrator: false });
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<UserRow> Users { get; } = [];

    public ObservableCollection<UserTypeInfo> UserTypes { get; } = [];

    /// <summary>The user types of the account being edited, as checkboxes.</summary>
    public ObservableCollection<SelectableItem> UserTypeChoices { get; } = [];

    /// <summary>The permissions of the user type being edited, as checkboxes.</summary>
    public ObservableCollection<SelectableItem> PermissionChoices { get; } = [];

    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand NewUserCommand { get; }
    public AsyncCommand SaveUserCommand { get; }
    public AsyncCommand ResetPasswordCommand { get; }
    public AsyncCommand NewTypeCommand { get; }
    public AsyncCommand SaveTypeCommand { get; }
    public AsyncCommand DeleteTypeCommand { get; }

    public string Status
    {
        get => status;
        private set => Set(ref status, value);
    }

    public UserRow? SelectedUser
    {
        get => selectedUser;
        set
        {
            if (!Set(ref selectedUser, value) || value is null) return;
            IsNewUser = false;
            UserName = value.Account.UserName;
            DisplayName = value.Account.DisplayName;
            UserIsActive = value.Account.IsActive;
            TemporaryPassword = string.Empty;
            FillTypeChoices(value.Account.Types.Select(type => type.UserTypeId));
            RaiseCommands();
        }
    }

    public bool IsNewUser
    {
        get => isNewUser;
        private set
        {
            if (Set(ref isNewUser, value)) OnPropertyChanged(nameof(UserEditorTitle));
        }
    }

    public string UserEditorTitle => isNewUser ? "New user" : "Selected user";

    public string UserName
    {
        get => userName;
        set => Set(ref userName, value);
    }

    public string DisplayName
    {
        get => displayName;
        set => Set(ref displayName, value);
    }

    public bool UserIsActive
    {
        get => userIsActive;
        set => Set(ref userIsActive, value);
    }

    /// <summary>For a new user or a reset: the user must replace it at the next sign-in.</summary>
    public string TemporaryPassword
    {
        get => temporaryPassword;
        set => Set(ref temporaryPassword, value);
    }

    public UserTypeInfo? SelectedType
    {
        get => selectedType;
        set
        {
            if (!Set(ref selectedType, value) || value is null) return;
            IsNewType = false;
            TypeName = value.Name;
            TypeDescription = value.Description ?? string.Empty;
            FillPermissionChoices(value.IsAdministrator ? permissions.Select(item => item.Code) : value.Permissions);
            OnPropertyChanged(nameof(IsTypeEditable));
            OnPropertyChanged(nameof(TypeNote));
            RaiseCommands();
        }
    }

    public bool IsNewType
    {
        get => isNewType;
        private set
        {
            if (Set(ref isNewType, value))
            {
                OnPropertyChanged(nameof(IsTypeEditable));
                OnPropertyChanged(nameof(TypeEditorTitle));
                OnPropertyChanged(nameof(TypeNote));
            }
        }
    }

    public string TypeEditorTitle => isNewType ? "New user type" : "Selected user type";

    /// <summary>The built-in Administrator type holds every permission and cannot be changed.</summary>
    public bool IsTypeEditable => isNewType || selectedType is { IsAdministrator: false };

    public string TypeNote => !isNewType && selectedType is { IsAdministrator: true }
        ? "Administrator is built in: it may do everything, and it cannot be changed or removed."
        : "Tick what users of this type may change. Everyone may view Cases, the Planning Board, the Timeline, NC files, tool tables and the tool library, and create Production Packages.";

    public string TypeName
    {
        get => typeName;
        set => Set(ref typeName, value);
    }

    public string TypeDescription
    {
        get => typeDescription;
        set => Set(ref typeDescription, value);
    }

    internal void AttachSession(IPlannerApiClient? client, string signedInUser)
    {
        actor = signedInUser;
        if (ReferenceEquals(api, client)) return;
        api = client;
        if (api is null)
        {
            Users.Clear();
            UserTypes.Clear();
            UserTypeChoices.Clear();
            PermissionChoices.Clear();
            Status = "Only administrators see this page.";
        }
        RaiseCommands();
        if (api is not null) _ = RefreshAsync();
    }

    internal async Task RefreshAsync()
    {
        if (api is null || isBusy) return;
        await RunAsync(async () =>
        {
            var permissionsTask = api.ListPermissionsAsync();
            var typesTask = api.ListUserTypesAsync();
            var usersTask = api.ListUsersAsync();
            await Task.WhenAll(permissionsTask, typesTask, usersTask);
            permissions = await permissionsTask;
            ApplyTypes(await typesTask);
            ApplyUsers(await usersTask);
            Status = $"{Users.Count} users, {UserTypes.Count} user types. Signed in as {actor}.";
        });
    }

    private void BeginNewUser()
    {
        selectedUser = null;
        OnPropertyChanged(nameof(SelectedUser));
        IsNewUser = true;
        UserName = string.Empty;
        DisplayName = string.Empty;
        UserIsActive = true;
        TemporaryPassword = string.Empty;
        FillTypeChoices([]);
        Status = "Enter the new user's name, a temporary password and the user types, then Save.";
        RaiseCommands();
    }

    private async Task SaveUserAsync()
    {
        if (api is null) return;
        var types = UserTypeChoices.Where(choice => choice.IsSelected).Select(choice => choice.Key).ToArray();
        await RunAsync(async () =>
        {
            UserAccountInfo saved;
            if (isNewUser)
            {
                saved = await api.CreateUserAsync(UserName.Trim(), DisplayName.Trim(), TemporaryPassword, UserIsActive, types);
                Status = $"User {saved.UserName} created. Give them the temporary password; they choose their own at the first sign-in.";
            }
            else
            {
                saved = await api.UpdateUserAsync(
                    selectedUser!.Account.UserId, DisplayName.Trim(), UserIsActive, types, selectedUser.Account.Version);
                Status = $"User {saved.UserName} saved.";
            }
            ApplyUsers(await api.ListUsersAsync(), saved.UserId);
            ApplyTypes(await api.ListUserTypesAsync());
        });
    }

    private async Task ResetPasswordAsync()
    {
        if (api is null || selectedUser is null) return;
        await RunAsync(async () =>
        {
            await api.ResetUserPasswordAsync(selectedUser.Account.UserId, TemporaryPassword);
            Status = $"Temporary password set for {selectedUser.Account.UserName}: their open sessions ended, and they choose a new password at the next sign-in.";
            TemporaryPassword = string.Empty;
            ApplyUsers(await api.ListUsersAsync(), selectedUser.Account.UserId);
        });
    }

    private void BeginNewType()
    {
        selectedType = null;
        OnPropertyChanged(nameof(SelectedType));
        IsNewType = true;
        TypeName = string.Empty;
        TypeDescription = string.Empty;
        FillPermissionChoices([]);
        Status = "Name the new user type and tick its permissions, then Save.";
        RaiseCommands();
    }

    private async Task SaveTypeAsync()
    {
        if (api is null || !IsTypeEditable) return;
        var granted = PermissionChoices.Where(choice => choice.IsSelected).Select(choice => choice.Key).ToArray();
        var description = string.IsNullOrWhiteSpace(TypeDescription) ? null : TypeDescription.Trim();
        await RunAsync(async () =>
        {
            var saved = isNewType
                ? await api.CreateUserTypeAsync(TypeName.Trim(), description, granted)
                : await api.UpdateUserTypeAsync(selectedType!.UserTypeId, TypeName.Trim(), description, granted, selectedType.Version);
            Status = saved.UserCount == 0
                ? $"User type {saved.Name} saved."
                : $"User type {saved.Name} saved; its {saved.UserCount} users get the change within a minute.";
            ApplyTypes(await api.ListUserTypesAsync(), saved.UserTypeId);
            ApplyUsers(await api.ListUsersAsync(), selectedUser?.Account.UserId);
        });
    }

    private async Task DeleteTypeAsync()
    {
        if (api is null || selectedType is null) return;
        var removed = selectedType;
        await RunAsync(async () =>
        {
            await api.DeleteUserTypeAsync(removed.UserTypeId, removed.Version);
            Status = $"User type {removed.Name} removed.";
            ApplyTypes(await api.ListUserTypesAsync());
        });
    }

    private void ApplyUsers(IReadOnlyList<UserAccountInfo> users, string? selectId = null)
    {
        Users.Clear();
        foreach (var user in users.OrderBy(user => user.UserName, StringComparer.OrdinalIgnoreCase))
            Users.Add(new UserRow(user));
        var keep = selectId ?? selectedUser?.Account.UserId;
        selectedUser = null;
        SelectedUser = Users.FirstOrDefault(row => row.Account.UserId == keep);
        if (selectedUser is null && !isNewUser) FillTypeChoices([]);
        if (selectedUser is not null) IsNewUser = false;
        RaiseCommands();
    }

    private void ApplyTypes(IReadOnlyList<UserTypeInfo> types, string? selectId = null)
    {
        UserTypes.Clear();
        foreach (var type in types.OrderByDescending(type => type.IsAdministrator).ThenBy(type => type.Name, StringComparer.OrdinalIgnoreCase))
            UserTypes.Add(type);
        var keep = selectId ?? selectedType?.UserTypeId;
        selectedType = null;
        SelectedType = UserTypes.FirstOrDefault(type => type.UserTypeId == keep);
        if (selectedType is not null) IsNewType = false;
        else if (!isNewType) FillPermissionChoices([]);
        // Keep the user's type checkboxes in step with the renamed, added or removed types.
        FillTypeChoices(UserTypeChoices.Where(choice => choice.IsSelected).Select(choice => choice.Key).ToArray());
        OnPropertyChanged(nameof(IsTypeEditable));
        OnPropertyChanged(nameof(TypeNote));
        RaiseCommands();
    }

    private void FillTypeChoices(IEnumerable<string> selectedIds)
    {
        var selected = selectedIds.ToHashSet(StringComparer.Ordinal);
        UserTypeChoices.Clear();
        foreach (var type in UserTypes)
            UserTypeChoices.Add(new SelectableItem(type.UserTypeId, type.Name, type.Description ?? string.Empty, selected.Contains(type.UserTypeId), true));
    }

    private void FillPermissionChoices(IEnumerable<string> granted)
    {
        var selected = granted.ToHashSet(StringComparer.Ordinal);
        var editable = IsTypeEditable;
        PermissionChoices.Clear();
        foreach (var permission in permissions)
            PermissionChoices.Add(new SelectableItem(permission.Code, permission.Name, permission.Description, selected.Contains(permission.Code), editable));
    }

    private async Task RunAsync(Func<Task> action)
    {
        isBusy = true;
        RaiseCommands();
        try
        {
            await action();
        }
        catch (PlannerApiException exception)
        {
            Status = exception.Conflict is null
                ? exception.Message
                : $"{exception.Message} {exception.Conflict.Advice}";
        }
        catch (Exception exception) when (exception is PlannerProtocolException or HttpRequestException or TaskCanceledException)
        {
            Status = exception is TaskCanceledException
                ? "The Server did not respond before the client timeout."
                : exception is HttpRequestException ? "The configured Server could not be reached." : exception.Message;
        }
        finally
        {
            isBusy = false;
            RaiseCommands();
        }
    }

    private void RaiseCommands()
    {
        RefreshCommand.RaiseCanExecuteChanged();
        NewUserCommand.RaiseCanExecuteChanged();
        SaveUserCommand.RaiseCanExecuteChanged();
        ResetPasswordCommand.RaiseCanExecuteChanged();
        NewTypeCommand.RaiseCanExecuteChanged();
        SaveTypeCommand.RaiseCanExecuteChanged();
        DeleteTypeCommand.RaiseCanExecuteChanged();
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

/// <summary>One account in the users list.</summary>
internal sealed class UserRow(UserAccountInfo account)
{
    public UserAccountInfo Account { get; } = account;
    public string UserName => Account.UserName;
    public string DisplayName => Account.DisplayName;
    public string TypesText => Account.Types.Count == 0 ? "—" : string.Join(", ", Account.Types.Select(type => type.Name));
    public string StateText => !Account.IsActive
        ? "Inactive"
        : Account.MustChangePassword ? "Must change password" : "Active";
    public string LastSignInText => Account.LastSignInAt?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "Never";
}

/// <summary>A checkbox: a user type of a user, or a permission of a user type.</summary>
internal sealed class SelectableItem(string key, string name, string description, bool isSelected, bool isEnabled) : INotifyPropertyChanged
{
    private bool isSelected = isSelected;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Key { get; } = key;
    public string Name { get; } = name;
    public string Description { get; } = description;
    public bool IsEnabled { get; } = isEnabled;

    public bool IsSelected
    {
        get => isSelected;
        set
        {
            if (isSelected == value) return;
            isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }
}
