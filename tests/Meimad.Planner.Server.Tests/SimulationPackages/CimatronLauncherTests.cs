using Meimad.CimatronExporter;

namespace Meimad.Planner.Server.Tests.SimulationPackages;

/// <summary>
/// The one-click Cimatron → Meimad launcher without Cimatron or a window: how the toolbar button, the shortcut and the
/// post-processor hook start it, where a package goes, where the target/fixture STL is found, what it remembers, and the
/// lock that keeps the exporter's own post from starting another export.
/// </summary>
public sealed class CimatronLauncherTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("meimad-launcher-").FullName;

    public void Dispose() => Directory.Delete(folder, true);

    [Fact]
    public void The_window_starts_without_arguments_with_ui_or_from_the_post_and_the_console_keeps_its_command_line()
    {
        Assert.Equal(new LauncherStart(false, false, null, null, null, null, null, null), LauncherPlan.ParseStart([]));
        Assert.Null(LauncherPlan.ParseStart(["--active-cimatron", "--output", @"C:\x", "--post", "P", "--ucs", "MODEL"]));
        Assert.Null(LauncherPlan.ParseStart(["--probe"]));

        var fromPost = LauncherPlan.ParseStart(["--from-post", "--toolpath", "TP_MODEL", "--post", "Mazak_5x", "--auto"])!;
        Assert.True(fromPost.FromPost);
        Assert.True(fromPost.Auto);
        Assert.Equal(("TP_MODEL", "Mazak_5x"), (fromPost.Toolpath, fromPost.Post));

        // The detached window gets the same presets and never --from-post again (that would start yet another window).
        var relaunch = LauncherPlan.RelaunchArguments(fromPost);
        Assert.Equal(["--ui", "--toolpath", "TP_MODEL", "--post", "Mazak_5x", "--auto"], relaunch);
        Assert.False(LauncherPlan.ParseStart(relaunch)!.FromPost);
    }

    [Fact]
    public void The_case_name_comes_from_the_document_name()
    {
        Assert.Equal("30P450172200-002", LauncherPlan.GuessCaseName("30P450172200-002SOFI_NC1 - Copy"));
        Assert.Equal("30P460168000-001", LauncherPlan.GuessCaseName("30P460168000-001SOFIIIIIII_NC2.elt"));
        Assert.Equal("PN-1", LauncherPlan.GuessCaseName("PN-1_NC"));
        Assert.Null(LauncherPlan.GuessCaseName("  "));
    }

    [Fact]
    public void The_package_goes_to_the_operation_folder_of_the_case_like_the_bat_launcher()
    {
        Assert.Equal(Path.Combine(@"\\192.168.0.240\data\customers files\DPD\PN-1", "Gcode", "PN-1", "010"),
            LauncherPlan.OutputFolder(@"\\192.168.0.240\data\customers files\DPD\PN-1", "PN-1", "010"));
        Assert.Equal(Path.Combine(@"C:\Cases\A", "Gcode", "A_B", "010"), LauncherPlan.OutputFolder(@"C:\Cases\A ", "A/B", " 010 "));
    }

    [Fact]
    public void The_target_and_fixture_stl_are_found_beside_the_nc_document()
    {
        var document = Path.Combine(folder, "30P450172200-002SOFI_NC1.elt");
        File.WriteAllText(document, "");
        Assert.Null(LauncherPlan.FindGeometryFolder(document, "TP_MODEL"));

        var root = Directory.CreateDirectory(Path.Combine(folder, LauncherPlan.GeometryFolderName)).FullName;
        File.WriteAllText(Path.Combine(root, "TARGET.stl"), "solid");
        Assert.Equal(root, LauncherPlan.FindGeometryFolder(document, "TP_MODEL"));

        // A folder per toolpath wins over the shared one.
        var perToolpath = Directory.CreateDirectory(Path.Combine(root, "TP_MODEL")).FullName;
        File.WriteAllText(Path.Combine(perToolpath, "FIXTURE_VISE.stl"), "solid");
        Assert.Equal(perToolpath, LauncherPlan.FindGeometryFolder(document, "TP_MODEL"));
        Assert.Equal(root, LauncherPlan.FindGeometryFolder(document, "TP_MODEL_0"));
        Assert.Null(LauncherPlan.FindGeometryFolder(null, "TP_MODEL"));
    }

    [Fact]
    public void The_window_runs_the_console_command_line_and_names_a_missing_field()
    {
        var form = Form();
        var (options, error) = LauncherPlan.Options(form);
        Assert.Null(error);
        Assert.True(options!.ActiveCimatron);
        Assert.Equal((Path.Combine(@"C:\Cases\PN-1", "Gcode", "PN-1", "010"), "Mazak_5x", "MODEL", "TP_MODEL", "010", "PN-1", @"C:\geo"),
            (options.Output, options.Post, options.Ucs, options.Toolpath, options.OperationNumber, options.PartNumber, options.GeometryFolder));

        Assert.Contains("Operation", LauncherPlan.Options(form with { Operation = " " }).Error);
        Assert.Contains("toolpath", LauncherPlan.Options(form with { Toolpath = "" }).Error);
        Assert.Contains("Case working folder", LauncherPlan.Options(form with { CaseFolder = @"Cases\PN-1" }).Error);
        Assert.Null(LauncherPlan.Options(form with { GeometryFolder = "" }).Options!.GeometryFolder);
    }

    [Fact]
    public void The_window_remembers_posts_cases_and_operations_per_document()
    {
        var path = Path.Combine(folder, "settings", "settings.json");
        var settings = LauncherSettings.Load(path);
        Assert.Empty(settings.Posts);
        Assert.Equal("MODEL", settings.Ucs);

        var now = new DateTimeOffset(2026, 10, 12, 15, 0, 0, TimeSpan.Zero);
        settings.Remember(Form(), now);
        settings.Remember(Form() with { Toolpath = "TP_MODEL_0", Operation = "020", Post = "Haas_UMC" }, now.AddMinutes(1));
        settings.Remember(Form() with { Post = "mazak_5x" }, now.AddMinutes(2));
        Assert.True(settings.Save(path));

        var loaded = LauncherSettings.Load(path);
        Assert.Equal(["mazak_5x", "Haas_UMC"], loaded.Posts);
        Assert.Equal(@"C:\Cases\PN-1", loaded.LastCaseFolder);
        var memory = loaded.For(@"C:\NC\PN-1_NC1.elt")!;
        Assert.Equal(("PN-1", "TP_MODEL"), (memory.CaseName, memory.Toolpath));
        Assert.Equal("020", memory.Operations["tp_model_0"]);
        Assert.Equal("010", memory.Operations["TP_MODEL"]);
        Assert.NotNull(loaded.For(@"c:\nc\pn-1_nc1.ELT"));

        for (var index = 0; index < 12; index++) loaded.Remember(Form() with { Post = $"POST_{index}" }, now);
        Assert.Equal(10, loaded.Posts.Count);
        Assert.Equal("POST_11", loaded.Posts[0]);

        File.WriteAllText(path, "{ not json");
        Assert.Empty(LauncherSettings.Load(path).Posts);
    }

    [Fact]
    public void The_export_lock_tells_the_post_hook_that_the_post_is_the_exporters_own()
    {
        var name = $@"Local\Meimad.CimatronExporter.Test.{Guid.NewGuid():N}";
        Assert.False(ExporterLocks.IsHeld(name));

        using var taken = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            using var held = ExporterLocks.TryTake(name);
            taken.Set();
            release.Wait();
        });
        holder.Start();
        taken.Wait();
        try
        {
            Assert.True(ExporterLocks.IsHeld(name));
            Assert.Null(ExporterLocks.TryTake(name));
        }
        finally
        {
            release.Set();
            holder.Join();
        }

        Assert.False(ExporterLocks.IsHeld(name));
    }

    private static LauncherForm Form() => new(
        "PN-1_NC1", @"C:\NC\PN-1_NC1.elt", @"C:\Cases\PN-1", "PN-1", "010", "TP_MODEL", "Mazak_5x", "MODEL", @"C:\geo");
}
