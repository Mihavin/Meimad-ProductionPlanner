namespace Meimad.Planner.Server.Application.EditMode;

internal sealed record EditAuthority(string ClientId, long Generation);

internal sealed class EditModeMutationException : Exception
{
    internal EditModeMutationException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    internal string Code { get; }
}
