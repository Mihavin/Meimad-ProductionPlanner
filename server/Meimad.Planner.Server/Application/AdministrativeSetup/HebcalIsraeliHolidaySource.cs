using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Meimad.Planner.Server.Domain.AdministrativeSetup;

namespace Meimad.Planner.Server.Application.AdministrativeSetup;

internal sealed class HebcalIsraeliHolidaySource(HttpClient httpClient) : IIsraeliHolidaySource
{
    public string ProviderName => "hebcal";

    public async Task<IReadOnlyList<IsraeliHolidaySourceItem>> FetchAsync(
        int fromYear, int toYear, CancellationToken cancellationToken)
    {
        if (fromYear < 1900 || toYear > 2200 || toYear < fromYear || toYear - fromYear > 10)
            throw new ArgumentOutOfRangeException(nameof(fromYear), "Holiday refresh supports a range of at most 11 Gregorian years from 1900 through 2200.");

        var results = new Dictionary<string, IsraeliHolidaySourceItem>(StringComparer.Ordinal);
        try
        {
            for (var year = fromYear; year <= toYear; year++)
            {
                var path = $"hebcal?v=1&cfg=json&year={year.ToString(CultureInfo.InvariantCulture)}&yt=G&month=x&maj=on&mod=on&i=on";
                var response = await httpClient.GetFromJsonAsync<HebcalResponse>(path, cancellationToken)
                    ?? throw new IsraeliHolidaySourceException("Hebcal returned an empty response.");
                foreach (var item in (response.Items ?? []).Where(item =>
                             string.Equals(item.Category, "holiday", StringComparison.OrdinalIgnoreCase)))
                {
                    if (!DateOnly.TryParseExact(item.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                            DateTimeStyles.None, out var date) || string.IsNullOrWhiteSpace(item.Title))
                        continue;
                    var externalId = $"hebcal:{date:yyyy-MM-dd}:{NormalizeKey(item.Title)}";
                    var status = item.YomTov || item.Title.Contains("Yom HaAtzma", StringComparison.OrdinalIgnoreCase)
                        ? IsraeliHolidayStatus.NonWorking
                        : IsraeliHolidayStatus.Working;
                    results[externalId] = new(externalId, date, item.Title.Trim(), status);
                }
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                                           or System.Text.Json.JsonException)
        {
            throw new IsraeliHolidaySourceException(
                "The online Israeli holiday source could not be reached or returned invalid data. The local cache was not changed.",
                exception);
        }

        return results.Values.GroupBy(value => value.Date).Select(group => new IsraeliHolidaySourceItem(
                $"hebcal:{group.Key:yyyy-MM-dd}", group.Key,
                string.Join(" / ", group.Select(value => value.Name).Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)),
                group.Any(value => value.Status == IsraeliHolidayStatus.NonWorking)
                    ? IsraeliHolidayStatus.NonWorking : IsraeliHolidayStatus.Working))
            .OrderBy(value => value.Date).ToArray();
    }

    private static string NormalizeKey(string value) =>
        string.Join('-', value.Trim().ToLowerInvariant().Split(
            [' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));

    private sealed record HebcalResponse([property: JsonPropertyName("items")] IReadOnlyList<HebcalItem>? Items);
    private sealed record HebcalItem(
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("date")] string Date,
        [property: JsonPropertyName("category")] string Category,
        [property: JsonPropertyName("yomtov")] bool YomTov = false);
}
