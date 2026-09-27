namespace Meimad.Planner.Server.Application.EditMode;

/// <summary>
/// Who makes a change. Single Edit Mode is retired (owner decision 2026-09-27): the API authorizes the
/// signed-in user for the change's permission and passes the user here; the generation is no longer
/// checked. Conflicts between parallel users are caught by record versions instead.
/// </summary>
internal sealed record EditAuthority(string ClientId, long Generation, string? UserId = null);

/// <summary>The signed-in user an <see cref="EditAuthority"/> carries, as the actor stored with a change.</summary>
internal static class SignedInActor
{
    internal static string Require(EditAuthority authority) =>
        authority.UserId ?? throw new EditModeMutationException("sign_in_required", "Sign in to change data.");
}

internal sealed class EditModeMutationException : Exception
{
    internal EditModeMutationException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    internal string Code { get; }
}
