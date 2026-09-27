using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Meimad.Planner.Server.Application.Accounts;

/// <summary>
/// Sign-in with a Meimad user name and password (owner decision 2026-09-27). A signed-in client holds
/// a random session token; the Server keeps only its SHA-256 hash. A session ends after 12 hours
/// without use, at sign-out, when the account is deactivated or when an administrator resets the
/// password. Five wrong passwords lock the account for five minutes.
/// </summary>
internal sealed class AccountService(IAccountRepository repository, TimeProvider timeProvider)
{
    internal static readonly TimeSpan IdleTimeout = TimeSpan.FromHours(12);
    private const int MaximumFailures = 5;
    private static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(20);

    // Resolved sessions for a short time, so a board refresh does not read the accounts each call.
    private readonly ConcurrentDictionary<string, (SignedInUser User, DateTimeOffset CachedAt)> sessions = new(StringComparer.Ordinal);

    internal Task<bool> AnyAccountAsync(CancellationToken cancellationToken) => repository.AnyAccountAsync(cancellationToken);

    internal async Task<SignInResult> SignInAsync(string? userName, string? password, string? clientId, CancellationToken cancellationToken)
    {
        var failure = new AccountException(StatusCodes.Status401Unauthorized, "sign_in_failed",
            "The user name or password is wrong.");
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrEmpty(password)) throw failure;
        var now = timeProvider.GetUtcNow();
        var account = await repository.FindForSignInAsync(userName, cancellationToken) ?? throw failure;
        if (account.LockedUntil is { } locked && locked > now)
            throw new AccountException(StatusCodes.Status423Locked, "account_locked",
                $"Too many wrong passwords. Try again after {locked.ToLocalTime():HH:mm}, or ask an administrator to reset the password.");
        if (!PasswordHasher.Verify(password, account.PasswordHash))
        {
            var lockUntil = account.FailedSignIns + 1 >= MaximumFailures ? now + LockDuration : (DateTimeOffset?)null;
            await repository.RecordSignInAsync(account.UserId, false, lockUntil, now, cancellationToken);
            throw failure;
        }
        if (!account.IsActive)
            throw new AccountException(StatusCodes.Status403Forbidden, "account_inactive",
                "This account is switched off. Ask an administrator to switch it on.");

        await repository.RecordSignInAsync(account.UserId, true, null, now, cancellationToken);
        return await StartSessionAsync(account.UserId, clientId, now, cancellationToken);
    }

    /// <summary>Creates the first administrator of a new installation and signs it in.</summary>
    internal async Task<SignInResult> CreateFirstAdministratorAsync(
        string? userName, string? displayName, string? password, string? clientId, CancellationToken cancellationToken)
    {
        var name = RequiredText(userName, "userName", 64);
        PasswordHasher.Validate(password);
        var now = timeProvider.GetUtcNow();
        var userId = Guid.NewGuid().ToString("N");
        if (!await repository.CreateFirstAdministratorAsync(
                userId, name, OptionalText(displayName, 120) ?? name, PasswordHasher.Hash(password!), now, cancellationToken))
        {
            throw new AccountException(StatusCodes.Status409Conflict, "accounts_exist",
                "An administrator already exists. Sign in with an account, or ask the administrator for one.");
        }
        return await StartSessionAsync(userId, clientId, now, cancellationToken);
    }

    internal async Task<SignedInUser?> ResolveAsync(string token, CancellationToken cancellationToken)
    {
        var hash = HashToken(token);
        var now = timeProvider.GetUtcNow();
        if (sessions.TryGetValue(hash, out var cached) && now - cached.CachedAt < CacheLifetime) return cached.User;
        var user = await repository.ResolveSessionAsync(hash, now, IdleTimeout, cancellationToken);
        if (user is null) sessions.TryRemove(hash, out _);
        else sessions[hash] = (user, now);
        return user;
    }

    internal async Task SignOutAsync(string token, CancellationToken cancellationToken)
    {
        var hash = HashToken(token);
        sessions.TryRemove(hash, out _);
        await repository.RevokeSessionAsync(hash, timeProvider.GetUtcNow(), cancellationToken);
    }

    internal async Task ChangeOwnPasswordAsync(
        SignedInUser user, string? currentPassword, string? newPassword, CancellationToken cancellationToken)
    {
        var account = await repository.FindForSignInAsync(user.UserName, cancellationToken)
            ?? throw new AccountException(StatusCodes.Status404NotFound, "resource_not_found", "The user account was not found.");
        if (string.IsNullOrEmpty(currentPassword) || !PasswordHasher.Verify(currentPassword, account.PasswordHash))
            throw new AccountException(StatusCodes.Status422UnprocessableEntity, "current_password_wrong",
                "The current password is wrong.");
        PasswordHasher.Validate(newPassword);
        if (PasswordHasher.Verify(newPassword!, account.PasswordHash))
            throw new AccountException(StatusCodes.Status422UnprocessableEntity, "password_unchanged",
                "Choose a password different from the current one.");
        await repository.SetPasswordAsync(user.UserId, PasswordHasher.Hash(newPassword!), mustChange: false,
            endSessions: false, user.UserName, timeProvider.GetUtcNow(), cancellationToken);
        sessions.Clear();
    }

    internal Task<IReadOnlyList<UserAccount>> ListUsersAsync(CancellationToken cancellationToken) =>
        repository.ListUsersAsync(cancellationToken);

    internal Task<IReadOnlyList<UserType>> ListTypesAsync(CancellationToken cancellationToken) =>
        repository.ListTypesAsync(cancellationToken);

    internal async Task<UserAccount> CreateUserAsync(
        string? userName, string? displayName, bool isActive, IReadOnlyList<string>? typeIds, string? password,
        SignedInUser actor, CancellationToken cancellationToken)
    {
        var name = RequiredText(userName, "userName", 64);
        if (name.Any(char.IsWhiteSpace))
            throw new AccountException(StatusCodes.Status422UnprocessableEntity, "user_name_invalid",
                "A user name has no spaces, for example 'rina' or 'd.cohen'.");
        PasswordHasher.Validate(password);
        var created = await repository.CreateUserAsync(
            Guid.NewGuid().ToString("N"), name,
            new UserAccountChange(OptionalText(displayName, 120) ?? name, isActive, typeIds ?? []),
            PasswordHasher.Hash(password!), actor.UserName, timeProvider.GetUtcNow(), cancellationToken);
        sessions.Clear();
        return created;
    }

    internal async Task<UserAccount> UpdateUserAsync(
        string userId, int expectedVersion, string? displayName, bool isActive, IReadOnlyList<string>? typeIds,
        SignedInUser actor, CancellationToken cancellationToken)
    {
        var updated = await repository.UpdateUserAsync(
            userId, expectedVersion,
            new UserAccountChange(RequiredText(displayName, "displayName", 120), isActive, typeIds ?? []),
            actor.UserName, timeProvider.GetUtcNow(), cancellationToken);
        sessions.Clear();
        return updated;
    }

    /// <summary>An administrator sets a new password; the user must change it at the next sign-in.</summary>
    internal async Task ResetPasswordAsync(string userId, string? password, SignedInUser actor, CancellationToken cancellationToken)
    {
        PasswordHasher.Validate(password);
        await repository.SetPasswordAsync(userId, PasswordHasher.Hash(password!), mustChange: true,
            endSessions: userId != actor.UserId, actor.UserName, timeProvider.GetUtcNow(), cancellationToken);
        sessions.Clear();
    }

    internal async Task<UserType> CreateTypeAsync(
        string? name, string? description, IReadOnlyList<string>? permissions, SignedInUser actor, CancellationToken cancellationToken)
    {
        var created = await repository.CreateTypeAsync(
            Guid.NewGuid().ToString("N"), TypeChange(name, description, permissions), actor.UserName,
            timeProvider.GetUtcNow(), cancellationToken);
        sessions.Clear();
        return created;
    }

    internal async Task<UserType> UpdateTypeAsync(
        string typeId, int expectedVersion, string? name, string? description, IReadOnlyList<string>? permissions,
        SignedInUser actor, CancellationToken cancellationToken)
    {
        var updated = await repository.UpdateTypeAsync(
            typeId, expectedVersion, TypeChange(name, description, permissions), actor.UserName,
            timeProvider.GetUtcNow(), cancellationToken);
        sessions.Clear();
        return updated;
    }

    internal async Task DeleteTypeAsync(string typeId, int expectedVersion, CancellationToken cancellationToken)
    {
        await repository.DeleteTypeAsync(typeId, expectedVersion, cancellationToken);
        sessions.Clear();
    }

    private async Task<SignInResult> StartSessionAsync(
        string userId, string? clientId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var expiresAt = now + IdleTimeout;
        await repository.CreateSessionAsync(Guid.NewGuid().ToString("N"), HashToken(token), userId,
            OptionalText(clientId, 120), now, expiresAt, cancellationToken);
        var user = await ResolveAsync(token, cancellationToken)
            ?? throw new InvalidOperationException("A new session could not be read back.");
        return new SignInResult(token, expiresAt, user);
    }

    private static UserTypeChange TypeChange(string? name, string? description, IReadOnlyList<string>? permissions)
    {
        var unknown = (permissions ?? []).Where(permission => !Permissions.IsKnown(permission)).ToArray();
        if (unknown.Length > 0)
            throw new AccountException(StatusCodes.Status422UnprocessableEntity, "permission_unknown",
                $"Unknown permission(s): {string.Join(", ", unknown)}.");
        return new UserTypeChange(RequiredText(name, "name", 80), OptionalText(description, 500), permissions ?? []);
    }

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private static string RequiredText(string? value, string field, int maximum) =>
        OptionalText(value, maximum) ?? throw new AccountException(StatusCodes.Status422UnprocessableEntity,
            "required_value_missing", $"{field} is required.");

    private static string? OptionalText(string? value, int maximum)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        if (text.Length > maximum)
            throw new AccountException(StatusCodes.Status422UnprocessableEntity, "value_too_long",
                $"A value may have at most {maximum} characters.");
        return text;
    }
}
