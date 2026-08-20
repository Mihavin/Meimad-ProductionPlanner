using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Domain.Postprocessors;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meimad.Planner.Server.Application.Postprocessors;

internal sealed class PostprocessorService
{
    private readonly IPostprocessorRepository repository;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<PostprocessorService> logger;

    public PostprocessorService(
        IPostprocessorRepository repository,
        TimeProvider timeProvider,
        ILogger<PostprocessorService>? logger = null)
    {
        this.repository = repository;
        this.timeProvider = timeProvider;
        this.logger = logger ?? NullLogger<PostprocessorService>.Instance;
    }

    internal async Task<Postprocessor> CreateAsync(
        CreatePostprocessorCommand command,
        EditAuthority authority,
        CancellationToken token = default)
    {
        var values = PostprocessorValidator.ValidateAndNormalize(new(
            command.Name,
            command.Description,
            command.IsActive));
        var now = timeProvider.GetUtcNow();
        var created = await repository.CreateAsync(new Postprocessor(
            Guid.NewGuid().ToString("N"),
            values.Name,
            values.Description,
            values.IsActive,
            1,
            now,
            now), authority, token);
        logger.LogInformation(
            "Created Postprocessor {PostprocessorId}; active={IsActive}.",
            created.PostprocessorId,
            created.IsActive);
        return created;
    }

    internal Task<Postprocessor?> GetByIdAsync(string id, CancellationToken token = default) =>
        repository.GetByIdAsync(id, token);

    internal Task<IReadOnlyList<Postprocessor>> ListAsync(CancellationToken token = default) =>
        repository.ListAsync(token);

    internal async Task<Postprocessor> UpdateAsync(
        string id,
        int expectedVersion,
        UpdatePostprocessorCommand command,
        EditAuthority authority,
        CancellationToken token = default)
    {
        var current = await repository.GetByIdAsync(id, token)
            ?? throw new PostprocessorNotFoundException(id);
        var values = PostprocessorValidator.ValidateAndNormalize(new(
            Select(command.Name, current.Name),
            Select(command.Description, current.Description),
            Select(command.IsActive, current.IsActive)));
        var candidate = current with
        {
            Name = values.Name,
            Description = values.Description,
            IsActive = values.IsActive,
            Version = expectedVersion + 1,
            UpdatedAt = timeProvider.GetUtcNow()
        };
        var saved = await repository.UpdateAsync(candidate, expectedVersion, authority, token)
            ?? throw new PostprocessorVersionConflictException(id, expectedVersion);
        logger.LogInformation(
            "Updated Postprocessor {PostprocessorId}; active={IsActive}.",
            saved.PostprocessorId,
            saved.IsActive);
        return saved;
    }

    internal async Task<bool> DeleteAsync(
        string id,
        EditAuthority authority,
        CancellationToken token = default)
    {
        var deleted = await repository.DeleteAsync(id, authority, token);
        if (deleted)
        {
            logger.LogInformation("Deleted Postprocessor {PostprocessorId}.", id);
        }

        return deleted;
    }

    private static T Select<T>(PostprocessorField<T> field, T current) =>
        field.IsSpecified ? field.Value : current;
}

internal sealed class PostprocessorNotFoundException(string id)
    : Exception($"Postprocessor '{id}' was not found.");

internal sealed class PostprocessorNameConflictException(string name)
    : Exception($"Postprocessor name '{name}' already exists.");

internal sealed class PostprocessorVersionConflictException(string id, int version)
    : Exception($"Postprocessor '{id}' is no longer at version {version}.");

internal sealed class PostprocessorInUseException(string id)
    : Exception($"Postprocessor '{id}' is assigned to one or more Machines.");

internal sealed class PostprocessorReferenceNotFoundException(string id)
    : Exception($"Active Postprocessor '{id}' was not found.");
