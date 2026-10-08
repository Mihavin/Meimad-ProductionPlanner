using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using Meimad.Planner.Client.Windows.Themes;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

public sealed class WorkbenchIconsTests
{
    private static readonly string[] ThemeNames = ["graphite", "light"];

    [Fact]
    public void Every_glyph_inks_inside_the_same_24_unit_box_in_both_themes() => RunOnSta(() =>
    {
        foreach (var theme in ThemeNames)
        foreach (var glyph in WorkbenchIcons.Glyphs)
        {
            var image = WorkbenchIcons.Render(glyph, theme, Colors.Black);
            Assert.True(image.IsFrozen, glyph.Key);
            Assert.Equal(new Rect(0, 0, 24, 24), image.Drawing.Bounds);

            var ink = WorkbenchIcons.Ink(glyph, theme).Bounds;
            Assert.False(ink.IsEmpty, $"{glyph.Key} ({theme}) draws nothing");
            Assert.True(ink.Left >= -0.05 && ink.Top >= -0.05 && ink.Right <= 24.05 && ink.Bottom <= 24.05,
                $"{glyph.Key} ({theme}) leaves the grid: {ink}");
        }
    });

    [Fact]
    public void Keycap_cuts_details_through_the_shape_while_plate_engraves_them() => RunOnSta(() =>
    {
        var settings = WorkbenchIcons.Glyphs.Single(g => g.Key == "Settings");
        var keycap = WorkbenchIcons.Ink(settings, "graphite");
        var plate = WorkbenchIcons.Ink(settings, "light");

        // The gear is solid on the key with its centre ring cut out; on the plate it is an outline
        // with the ring engraved, so the hub stays empty.
        Assert.True(keycap.FillContains(new Point(12, 12)));
        Assert.False(keycap.FillContains(new Point(15, 12)));
        Assert.False(plate.FillContains(new Point(12, 12)));
        Assert.True(plate.FillContains(new Point(15, 12)));
    });

    [Fact]
    public void Theme_publishes_every_icon_the_xaml_references() => RunOnSta(() =>
    {
        var keys = WorkbenchIcons.Glyphs.Select(g => g.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());

        var resources = new ResourceDictionary();
        WorkbenchIcons.Apply(resources, "light", Colors.Black);
        var clientRoot = Path.Combine(FindRepositoryRoot(), "client-windows", "Meimad.Planner.Client.Windows");
        var referenced = Directory.EnumerateFiles(clientRoot, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(path => Regex.Matches(File.ReadAllText(path), @"\{DynamicResource (Icon\.\w+)\}").Select(m => m.Groups[1].Value))
            .Distinct()
            .ToList();

        Assert.NotEmpty(referenced);
        Assert.All(referenced, key => Assert.IsType<DrawingImage>(resources[key]));
    });

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static void RunOnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { error = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw new Xunit.Sdk.XunitException(error.ToString());
    }
}
