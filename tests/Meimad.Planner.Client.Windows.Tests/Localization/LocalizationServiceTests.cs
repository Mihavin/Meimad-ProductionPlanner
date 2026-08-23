using Meimad.Planner.Client.Windows.Localization;

namespace Meimad.Planner.Client.Windows.Tests.Localization;

public sealed class LocalizationServiceTests
{
    [Fact]
    public void Catalog_indexes_initialize_within_the_interactive_startup_budget()
    {
        var duration = LocalizationService.Current.InitializationDuration;

        Assert.True(
            duration <= TimeSpan.FromMilliseconds(750),
            $"Localization initialization took {duration.TotalMilliseconds:N0} ms; the budget is 750 ms.");
    }

    [Theory]
    [InlineData("he")]
    [InlineData("ru")]
    public void Catalog_covers_the_complete_application_surface(string language)
    {
        Assert.True(LocalizationService.Current.CatalogEntryCount(language) >= 2400);
    }

    [Theory]
    [InlineData("he", "Cases", "פריטים")]
    [InlineData("he", "Planning Board", "לוח תכנון")]
    [InlineData("he", "Setup", "הגדרות")]
    [InlineData("ru", "Cases", "Изделия")]
    [InlineData("ru", "Planning Board", "Доска планирования")]
    [InlineData("ru", "Setup", "Настройки")]
    public void Prominent_navigation_is_translated(
        string language,
        string source,
        string expected)
    {
        Assert.Equal(expected, LocalizationService.Current.Translate(language, source));
    }

    [Fact]
    public void Unknown_domain_values_are_preserved()
    {
        const string partNumber = "30P450045200-001";
        Assert.Equal(partNumber, LocalizationService.Current.Translate("he", partNumber));
        Assert.Equal(partNumber, LocalizationService.Current.Translate("ru", partNumber));
    }

    [Theory]
    [InlineData("he")]
    [InlineData("ru")]
    public void Interpolated_workflow_messages_are_localized_without_changing_values(string language)
    {
        var translated = LocalizationService.Current.Translate(language, "7 row(s) were safely skipped");

        Assert.Contains("7", translated, StringComparison.Ordinal);
        Assert.DoesNotContain("row(s) were safely skipped", translated, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("he")]
    [InlineData("ru")]
    public void Messages_composed_from_multiple_ui_literals_are_fully_localized(string language)
    {
        const string message =
            "PN-1 / OP10 is intended for Mill 5x. You selected Machine 01 (Mill 3x). " +
            "This does not change the operation route. Confirm only when this exception is intentional; " +
            "the Server will audit your identity, time, both types, and reason.";

        var translated = LocalizationService.Current.Translate(language, message);

        Assert.Contains("PN-1 / OP10", translated, StringComparison.Ordinal);
        Assert.Contains("Machine 01", translated, StringComparison.Ordinal);
        Assert.DoesNotContain("This does not change the operation route", translated, StringComparison.Ordinal);
    }
}
