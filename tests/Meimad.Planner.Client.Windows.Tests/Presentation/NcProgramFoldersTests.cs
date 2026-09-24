using System.IO;
using Meimad.Planner.Client.Windows.Api;
using Meimad.Planner.Client.Windows.Presentation.NcViewer;

namespace Meimad.Planner.Client.Windows.Tests.Presentation;

public sealed class NcProgramFoldersTests : IDisposable
{
    private readonly string workingFolder = Path.Combine(Path.GetTempPath(), "MeimadPlanner.NcFolders.Tests", Guid.NewGuid().ToString("N"), "Case WF");

    [Fact]
    public void Next_release_numbers_follow_the_server_numbering()
    {
        var catalog = Catalog();

        Assert.Equal(new NcProgramRevision(2, "post-haas", "Haas NGC", 3),
            NcProgramFolders.NextRevision(catalog, "post-haas", NcProgramFolders.LocalPostRevision));
        Assert.Equal(new NcProgramRevision(2, "post-doosan", "Doosan 3X", 2),
            NcProgramFolders.NextRevision(catalog, "post-doosan", NcProgramFolders.LocalPostRevision));
        Assert.Equal(new NcProgramRevision(2, "post-new", "New post", 1),
            NcProgramFolders.NextRevision(catalog, "post-new", NcProgramFolders.LocalPostRevision));
        Assert.Equal(new NcProgramRevision(3, "post-haas", "Haas NGC", 1),
            NcProgramFolders.NextRevision(catalog, "post-haas", NcProgramFolders.NewProcessRevision));

        // The first release of an Operation always creates process revision 1.
        var first = catalog with { ActiveProcessRevision = null, ProcessRevisions = [], Releases = [] };
        Assert.Equal(new NcProgramRevision(1, "post-haas", "Haas NGC", 1),
            NcProgramFolders.NextRevision(first, "post-haas", NcProgramFolders.LocalPostRevision));
    }

    [Fact]
    public async Task The_folder_is_working_folder_gcode_case_operation_revision_postprocessor_local_revision()
    {
        var folders = Folders(Catalog(), "post-haas");

        var next = await folders.NextReleaseAsync(null, null);

        Assert.Equal(Path.Combine(workingFolder, "Gcode", "Bearing housing", "10", "2", "Haas NGC", "3"), next.Path);
        Assert.True(Directory.Exists(next.Path));
        Assert.Equal(3, next.Revision.LocalRevision);

        var newProcess = await folders.NextReleaseAsync("post-doosan", NcProgramFolders.NewProcessRevision);
        Assert.Equal(Path.Combine(workingFolder, "Gcode", "Bearing housing", "10", "3", "Doosan 3X", "1"), newProcess.Path);

        var release = await folders.ReleaseFolderAsync(new NcProgramRevision(1, "post-haas", "Haas NGC", 1));
        Assert.Equal(Path.Combine(workingFolder, "Gcode", "Bearing housing", "10", "1", "Haas NGC", "1"), release.Path);
        Assert.True(Directory.Exists(release.Path));
    }

    [Fact]
    public void Case_and_postprocessor_names_become_safe_folder_names()
    {
        Assert.Equal("Haas_ NGC_5-axis", NcProgramFolders.Segment("Haas: NGC/5-axis"));
        Assert.Equal("_CON", NcProgramFolders.Segment("CON"));
        Assert.Equal("_nul.txt", NcProgramFolders.Segment("nul.txt"));
        Assert.Equal("Bracket", NcProgramFolders.Segment(" Bracket. "));
        Assert.Equal("_", NcProgramFolders.Segment("  "));
        Assert.Equal("PN-100", NcProgramFolders.CaseFolderName(CaseRecord() with { Name = " " }));
        Assert.Equal("Bearing housing", NcProgramFolders.CaseFolderName(CaseRecord()));
    }

    [Fact]
    public async Task A_case_without_a_full_working_folder_path_has_no_revision_folder()
    {
        var missing = new NcProgramFolders(
            _ => Task.FromResult(new NcProgramLocation(" ", "Bearing housing", 10, Catalog())), "post-haas");
        var relative = new NcProgramFolders(
            _ => Task.FromResult(new NcProgramLocation(@"Cases\PN-100", "Bearing housing", 10, Catalog())), "post-haas");

        var noFolder = await Assert.ThrowsAsync<InvalidOperationException>(() => missing.NextReleaseAsync(null, null));
        var notFull = await Assert.ThrowsAsync<InvalidOperationException>(() => relative.NextReleaseAsync(null, null));
        var noPost = await Assert.ThrowsAsync<InvalidOperationException>(() => Folders(Catalog(), null).NextReleaseAsync(null, null));

        Assert.Contains("no Working Folder", noFolder.Message, StringComparison.Ordinal);
        Assert.Contains("not a full path", notFull.Message, StringComparison.Ordinal);
        Assert.Contains("postprocessor", noPost.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_released_program_moves_to_the_folder_of_the_assigned_numbers_without_replacing_files()
    {
        var folders = Folders(Catalog(), "post-haas");
        var predicted = (await folders.NextReleaseAsync(null, null)).Path;
        var program = Path.Combine(predicted, "O1500.nc");
        File.WriteAllText(program, "O1500\nM30\n");

        var placement = await folders.PlaceReleasedProgramAsync(program, new NcProgramRevision(2, "post-haas", "Haas NGC", 4));

        var expected = Path.Combine(workingFolder, "Gcode", "Bearing housing", "10", "2", "Haas NGC", "4", "O1500.nc");
        Assert.True(placement.InOperationFolder);
        Assert.Equal(expected, placement.Path);
        Assert.True(File.Exists(expected));
        Assert.False(Directory.Exists(predicted));

        // A file already in the destination is never replaced.
        var other = Path.Combine(Path.GetDirectoryName(expected)!, "..", "..", "Haas NGC", "5");
        Directory.CreateDirectory(other);
        var draft = Path.Combine(other, "O1500.nc");
        File.WriteAllText(draft, "draft");
        var kept = await folders.PlaceReleasedProgramAsync(expected, new NcProgramRevision(2, "post-haas", "Haas NGC", 5));
        Assert.Equal(expected, kept.Path);
        Assert.Equal("draft", File.ReadAllText(draft));

        // A program from anywhere else (CAM output) stays where it is.
        var outside = Path.Combine(Path.GetDirectoryName(workingFolder)!, "cam-output.nc");
        File.WriteAllText(outside, "O2000\nM30\n");
        var untouched = await folders.PlaceReleasedProgramAsync(outside, new NcProgramRevision(2, "post-haas", "Haas NGC", 6));
        Assert.False(untouched.InOperationFolder);
        Assert.Equal(outside, untouched.Path);
        Assert.True(File.Exists(outside));
    }

    public void Dispose()
    {
        var root = Path.GetDirectoryName(workingFolder)!;
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private NcProgramFolders Folders(PlannerGCodeCatalog catalog, string? defaultPostprocessorId) =>
        new(_ => Task.FromResult(new NcProgramLocation(workingFolder, "Bearing housing", 10, catalog)), defaultPostprocessorId);

    private static PlannerGCodeCatalog Catalog()
    {
        var tools = new PlannerToolTableRelease(
            "tools-1", 1, "tools.csv", 10, new string('a', 64), DateTimeOffset.UtcNow, "planner", "Initial tools");
        var first = new PlannerProcessRevision("process-1", 1, false, DateTimeOffset.UtcNow, "planner", "Initial", 1, tools);
        var active = new PlannerProcessRevision("process-2", 2, true, DateTimeOffset.UtcNow, "planner", "Faster roughing", 1, tools);
        return new PlannerGCodeCatalog(
            "operation-1",
            active,
            [active, first],
            [
                new PlannerPostprocessorReleaseStatus("post-haas", "Haas NGC", true, "current", null, null),
                new PlannerPostprocessorReleaseStatus("post-doosan", "Doosan 3X", true, "current", null, null),
                new PlannerPostprocessorReleaseStatus("post-new", "New post", true, "missing", null, null)
            ],
            [
                Release("release-1", first, "post-haas", "Haas NGC", 1),
                Release("release-2", active, "post-haas", "Haas NGC", 1),
                Release("release-3", active, "post-haas", "Haas NGC", 2),
                Release("release-4", active, "post-doosan", "Doosan 3X", 1)
            ]);
    }

    private static PlannerGCodeRelease Release(
        string id, PlannerProcessRevision process, string postprocessorId, string postprocessorName, int localRevision) => new(
        id, process.ProcessRevisionId, process.ProcessRevisionNumber, postprocessorId, postprocessorName, localRevision,
        "O1500.nc", 100, new string('b', 64), DateTimeOffset.UtcNow, "planner", "LOCAL_POST_REVISION", "comment",
        process.ToolTable.ToolTableReleaseId, true, process.IsActive);

    private static PlannerCase CaseRecord() => new(
        "case-1", "PN-100", "Bearing housing", "A", "Acme", "PO-1", null, @"C:\Cases\PN-100", "Aluminium", "7075-T6",
        "Plate", "30 x 100 x 100 mm", 600, 120, null, true, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
}
