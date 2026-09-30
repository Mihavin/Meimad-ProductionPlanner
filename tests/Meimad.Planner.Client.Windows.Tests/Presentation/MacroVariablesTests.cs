using System.Text.Json;
using Meimad.Planner.Client.Windows.Presentation.NcViewer;
using Meimad.Planner.Client.Windows.Views;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

/// <summary>The NC viewer page's macro variables table as the host reads it for its window.</summary>
public sealed class MacroVariablesTests
{
    [Fact]
    public void Page_payload_becomes_the_window_rows_and_the_filter_matches_any_column()
    {
        using var document = JsonDocument.Parse("""
            { "open": true, "position": "Playback at row 8",
              "rows": [ { "variable": "#1", "value": "5", "scope": "Local", "setAt": "row 7" },
                        { "variable": "#5001", "value": "20", "scope": "System", "setAt": "read" }, 7 ] }
            """);

        var variables = NcViewerSession.MacroVariables(document.RootElement);

        Assert.NotNull(variables);
        Assert.True(variables.Open);
        Assert.Equal("Playback at row 8", variables.Position);
        Assert.Equal([new NcViewerMacroVariable("#1", "5", "Local", "row 7"), new NcViewerMacroVariable("#5001", "20", "System", "read")], variables.Rows);
        Assert.True(MacroVariablesWindow.Matches(variables.Rows[1], "system"));
        Assert.False(MacroVariablesWindow.Matches(variables.Rows[0], "5001"));
        Assert.Null(NcViewerSession.MacroVariables(JsonDocument.Parse("[]").RootElement));
    }
}
