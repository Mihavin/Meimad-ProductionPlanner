namespace Meimad.Planner.Server.Application.Postprocessors;

internal sealed record CreatePostprocessorCommand(
    string? Name,
    string? Description,
    bool? IsActive);

internal readonly record struct PostprocessorField<T>(bool IsSpecified, T Value)
{
    internal static PostprocessorField<T> Unspecified => new(false, default!);
    internal static PostprocessorField<T> Specified(T value) => new(true, value);
}

internal sealed record UpdatePostprocessorCommand(
    PostprocessorField<string?> Name,
    PostprocessorField<string?> Description,
    PostprocessorField<bool?> IsActive);
