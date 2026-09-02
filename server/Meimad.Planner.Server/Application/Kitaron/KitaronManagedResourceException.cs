namespace Meimad.Planner.Server.Application.Kitaron;

internal sealed class KitaronManagedResourceException : Exception
{
    internal KitaronManagedResourceException(string resourceType, string resourceId)
        : base($"{resourceType} '{resourceId}' is managed by Kitaron and is read-only in Meimad Planner.")
    {
        ResourceType = resourceType;
        ResourceId = resourceId;
    }

    internal string ResourceType { get; }

    internal string ResourceId { get; }
}
