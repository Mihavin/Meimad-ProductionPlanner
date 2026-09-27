namespace Meimad.Planner.Server.Application.Accounts;

/// <summary>An account as sign-in needs it: the stored password hash and the lockout state.</summary>
internal sealed record StoredAccount(
    string UserId,
    string UserName,
    string DisplayName,
    string PasswordHash,
    bool IsActive,
    bool MustChangePassword,
    int FailedSignIns,
    DateTimeOffset? LockedUntil);

internal sealed record UserAccountChange(
    string DisplayName,
    bool IsActive,
    IReadOnlyList<string> UserTypeIds);

internal sealed record UserTypeChange(
    string Name,
    string? Description,
    IReadOnlyList<string> Permissions);

internal interface IAccountRepository
{
    Task<bool> AnyAccountAsync(CancellationToken cancellationToken);

    Task<StoredAccount?> FindForSignInAsync(string userName, CancellationToken cancellationToken);

    Task RecordSignInAsync(string userId, bool succeeded, DateTimeOffset? lockedUntil, DateTimeOffset now, CancellationToken cancellationToken);

    Task CreateSessionAsync(string sessionId, string tokenHash, string userId, string? clientId,
        DateTimeOffset now, DateTimeOffset expiresAt, CancellationToken cancellationToken);

    /// <summary>The user of a live session, extending the session when it was last seen a while ago.</summary>
    Task<SignedInUser?> ResolveSessionAsync(string tokenHash, DateTimeOffset now, TimeSpan idleTimeout, CancellationToken cancellationToken);

    Task RevokeSessionAsync(string tokenHash, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Creates the first account as an administrator; false when an account already exists.</summary>
    Task<bool> CreateFirstAdministratorAsync(string userId, string userName, string displayName, string passwordHash,
        DateTimeOffset now, CancellationToken cancellationToken);

    Task<IReadOnlyList<UserAccount>> ListUsersAsync(CancellationToken cancellationToken);

    /// <summary>The display name of the account with <paramref name="userName"/>, to name who changed data.</summary>
    Task<string?> FindDisplayNameAsync(string userName, CancellationToken cancellationToken);

    Task<UserAccount?> GetUserAsync(string userId, CancellationToken cancellationToken);

    Task<UserAccount> CreateUserAsync(string userId, string userName, UserAccountChange change, string passwordHash,
        string actor, DateTimeOffset now, CancellationToken cancellationToken);

    Task<UserAccount> UpdateUserAsync(string userId, int expectedVersion, UserAccountChange change, string actor,
        DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Sets a password; a reset by an administrator also ends the user's sessions.</summary>
    Task SetPasswordAsync(string userId, string passwordHash, bool mustChange, bool endSessions, string actor,
        DateTimeOffset now, CancellationToken cancellationToken);

    Task<IReadOnlyList<UserType>> ListTypesAsync(CancellationToken cancellationToken);

    Task<UserType> CreateTypeAsync(string userTypeId, UserTypeChange change, string actor, DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<UserType> UpdateTypeAsync(string userTypeId, int expectedVersion, UserTypeChange change, string actor,
        DateTimeOffset now, CancellationToken cancellationToken);

    Task DeleteTypeAsync(string userTypeId, int expectedVersion, CancellationToken cancellationToken);
}
