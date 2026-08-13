using Meimad.Planner.Server.Domain.AdministrativeSetup;

namespace Meimad.Planner.Server.Application.AdministrativeSetup;

internal interface IIsraeliHolidaySource
{
    string ProviderName { get; }
    Task<IReadOnlyList<IsraeliHolidaySourceItem>> FetchAsync(
        int fromYear, int toYear, CancellationToken cancellationToken);
}

internal sealed class IsraeliHolidaySourceException(string message, Exception? inner = null)
    : Exception(message, inner);
