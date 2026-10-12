using Meimad.CimatronExporter;

// Cimatron's API objects are apartment-threaded: the whole export runs on one STA thread.
var exitCode = 2;
var worker = new Thread(() => exitCode = Run(args));
if (OperatingSystem.IsWindows()) worker.SetApartmentState(ApartmentState.STA);
worker.Start();
worker.Join();
return exitCode;

static int Run(string[] args)
{

    // Meimad.CimatronExporter: Cimatron -> Meimad simulation package (exporter definition §11–§12).
    // Exit codes: 0 package written, 1 blocked (nothing published), 2 usage error, 3 Cimatron not available.
    var (options, error) = ExportOptions.Parse(args);
    if (options is null)
    {
        Console.Error.WriteLine(error);
        Console.Error.WriteLine();
        Console.Error.WriteLine(ExportOptions.Usage);
        return 2;
    }

    CimatronComApi api;
    try
    {
        api = CimatronComApi.Connect(options.ProgId ?? Environment.GetEnvironmentVariable("MEIMAD_CIMATRON_PROGID"), options.CimatronFolder);
    }
    catch (CimatronApiException exception)
    {
        Console.Error.WriteLine($"{exception.Code}: {exception.Message}");
        if (options.Probe && OperatingSystem.IsWindows())
        {
            // The probe's output goes to the console and a redirected file alike.
            Console.WriteLine($"{exception.Code}: {exception.Message}");
            foreach (var line in CimatronDiscovery.Report()) Console.WriteLine(line);
            WriteApiListing(options);
        }
        return 3;
    }

    using (api)
    {
        if (options.Probe)
        {
            Console.WriteLine($"Cimatron {api.CimatronVersion ?? "(version unknown)"}, adapter {api.AdapterVersion}");
            Console.WriteLine($"Connected through {CimatronComApi.ConnectedThrough ?? "the registered " + CimatronComApi.AppAccessProgId}");
            if (OperatingSystem.IsWindows()) WriteApiListing(options);
            foreach (var (name, available) in api.Capabilities.All())
                Console.WriteLine($"  {(available ? "yes" : "no "),-3}  {name}");
            foreach (var (owner, member, available) in api.Probe())
                Console.WriteLine($"  {(available ? "yes" : "no "),-3}  {owner}.{member}");
            try
            {
                var document = api.GetActiveNcDocument();
                Console.WriteLine($"Active NC document: {document.Name} {document.FilePath}");
                foreach (var procedure in api.GetProcedures())
                    Console.WriteLine($"  procedure {procedure.Id} '{procedure.Name}' UCS {procedure.UcsName ?? "?"} toolpath {procedure.HasToolpath}");
                Console.WriteLine("UCS: " + string.Join(", ", api.GetUcsNames()));
            }
            catch (CimatronApiException exception)
            {
                Console.WriteLine($"{exception.Code}: {exception.Message}");
            }

            return 0;
        }

        var result = new PackageExporter(api, TimeProvider.System).Export(options);
        Console.WriteLine(result.Log.ToReport());
        return result.ExitCode;
    }
}

// The classes and members of Cimatron's type libraries, beside the exporter, for matching the late-bound names.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
static void WriteApiListing(ExportOptions options)
{
    var folder = CimatronTypeLibrary.FindProgramFolder(options.CimatronFolder);
    if (folder is null)
    {
        Console.WriteLine("Cimatron API listing: no Cimatron Program folder found.");
        return;
    }
    var target = Path.Combine(AppContext.BaseDirectory, "cimatron-api.txt");
    try
    {
        var lines = new List<string> { $"Cimatron Program folder: {folder}" };
        foreach (var library in CimatronTypeLibrary.ProbeLibraries) lines.AddRange(CimatronTypeLibrary.Describe(Path.Combine(folder, library)));
        File.WriteAllLines(target, lines);
        Console.WriteLine($"Cimatron API listing ({lines.Count} lines): {target}");
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
    {
        Console.WriteLine($"Cimatron API listing failed: {exception.Message}");
    }
}
