using Meimad.Planner.Server.Application.EditMode;
using Meimad.Planner.Server.Domain.MachineTypes;

namespace Meimad.Planner.Server.Application.MachineTypes;

internal sealed class MachineTypeService
{
    private readonly IMachineTypeRepository repository;
    private readonly TimeProvider timeProvider;

    public MachineTypeService(IMachineTypeRepository repository, TimeProvider timeProvider)
    {
        this.repository = repository;
        this.timeProvider = timeProvider;
    }

    internal async Task<MachineType> CreateAsync(CreateMachineTypeCommand command, EditAuthority authority, CancellationToken token = default)
    {
        var values = MachineTypeValidator.ValidateAndNormalize(new(command.Name, command.Capabilities));
        var now = timeProvider.GetUtcNow();
        return await repository.CreateAsync(new MachineType(
            Guid.NewGuid().ToString("N"), values.Name, values.Capabilities, 1, now, now), authority, token);
    }

    internal Task<MachineType?> GetByIdAsync(string id, CancellationToken token = default) =>
        repository.GetByIdAsync(id, token);

    internal Task<IReadOnlyList<MachineType>> ListAsync(CancellationToken token = default) =>
        repository.ListAsync(token);

    internal async Task<MachineType> UpdateAsync(
        string id,
        int expectedVersion,
        UpdateMachineTypeCommand command,
        EditAuthority authority,
        CancellationToken token = default)
    {
        var current = await repository.GetByIdAsync(id, token) ?? throw new MachineTypeNotFoundException(id);
        var values = MachineTypeValidator.ValidateAndNormalize(new(
            command.Name.IsSpecified ? command.Name.Value : current.Name,
            command.Capabilities.IsSpecified
                ? command.Capabilities.Value
                : current.Capabilities.Cast<string?>().ToArray()));
        var candidate = current with
        {
            Name = values.Name,
            Capabilities = values.Capabilities,
            Version = expectedVersion + 1,
            UpdatedAt = timeProvider.GetUtcNow()
        };
        return await repository.UpdateAsync(candidate, expectedVersion, authority, token)
            ?? throw new MachineTypeVersionConflictException(id, expectedVersion);
    }

    internal Task<bool> DeleteAsync(string id, EditAuthority authority, CancellationToken token = default) =>
        repository.DeleteAsync(id, authority, token);
}

internal sealed class MachineTypeNotFoundException(string id) : Exception($"Machine Type '{id}' was not found.");
internal sealed class MachineTypeNameConflictException(string name) : Exception($"Machine Type name '{name}' already exists.");
internal sealed class MachineTypeVersionConflictException(string id, int version) : Exception($"Machine Type '{id}' is no longer at version {version}.");
internal sealed class MachineTypeInUseException(string id) : Exception($"Machine Type '{id}' is used by a Machine or Operation requirement.");
internal sealed class MachineTypeCompatibilityException(string message) : Exception(message);
internal sealed class MachineTypeNameInUseException(string message) : Exception(message);
