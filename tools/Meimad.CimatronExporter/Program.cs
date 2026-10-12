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

    if (options.RegisterCimatron || options.UnregisterCimatron)
    {
        if (!OperatingSystem.IsWindows()) return 3;
        var folder = CimatronTypeLibrary.FindProgramFolder(options.CimatronFolder);
        if (folder is null)
        {
            Console.Error.WriteLine("cimatron_not_installed: no Cimatron Program folder found (use --cimatron-folder).");
            return 3;
        }

        // An administrator window registers for every user of the PC (HKEY_LOCAL_MACHINE), which administrator processes
        // also see; a normal window registers for the current user only.
        var allUsers = CimatronRunningApplication.Elevated;
        var scope = allUsers ? "every user of this PC (administrator window)" : $"user {Environment.UserName} (normal window; run it as administrator to register for every user)";
        Console.WriteLine($"{(options.RegisterCimatron ? "Registering" : "Removing")} Cimatron type libraries for {scope} from {folder}:");
        var lines = CimatronTypeLibrary.Register(folder, unregister: options.UnregisterCimatron, allUsers);
        foreach (var line in lines) Console.WriteLine(line);
        return lines.Any(line => line.TrimStart().StartsWith("failed", StringComparison.Ordinal)) ? 1 : 0;
    }

    CimatronApiException? connectFailure = null;
    CimatronComApi? connected = null;
    try
    {
        connected = CimatronComApi.Connect(options.ProgId ?? Environment.GetEnvironmentVariable("MEIMAD_CIMATRON_PROGID"), options.CimatronFolder);
    }
    catch (CimatronApiException exception)
    {
        connectFailure = exception;
    }

    if (options.Probe && OperatingSystem.IsWindows())
    {
        // The probe's output goes to the console and a redirected file alike.
        if (connectFailure is not null) Console.WriteLine($"{connectFailure.Code}: {connectFailure.Message}");
        Console.WriteLine("Connection attempts:");
        foreach (var line in CimatronComApi.ConnectTrace) Console.WriteLine("  " + line);
        foreach (var line in CimatronDiscovery.Report()) Console.WriteLine(line);
        WriteRegistrationStatus(options);
        WriteApiListing(options);
    }

    CimatronComApi api;
    try
    {
        api = connected ?? throw connectFailure!;
    }
    catch (CimatronApiException exception)
    {
        Console.Error.WriteLine($"{exception.Code}: {exception.Message}");
        return 3;
    }

    using (api)
    {
        if (options.Probe)
        {
            Console.WriteLine($"Cimatron {api.CimatronVersion ?? "(version unknown)"}, adapter {api.AdapterVersion}");
            Console.WriteLine($"Connected through {CimatronComApi.ConnectedThrough ?? "the registered " + CimatronComApi.AppAccessProgId}");
            foreach (var (name, available) in api.Capabilities.All())
                Console.WriteLine($"  {(available ? "yes" : "no "),-3}  {name}");
            foreach (var (owner, member, available) in api.Probe())
                Console.WriteLine($"  {(available ? "yes" : "no "),-3}  {owner}.{member}");
            foreach (var line in api.ProbeDetails()) Console.WriteLine(line);
            try
            {
                var document = api.GetActiveNcDocument();
                Console.WriteLine($"Active NC document: {document.Name} {document.FilePath}");
                var stockFiles = StockFileDiscovery.Find(document.FilePath);
                Console.WriteLine($"Stock STL files beside the document: {(stockFiles.Count == 0 ? "none" : string.Join(", ", stockFiles))}");
                foreach (var procedure in api.GetProcedures())
                    Console.WriteLine($"  procedure {procedure.Id} '{procedure.Name}' UCS {procedure.UcsName ?? "?"} toolpath {procedure.HasToolpath}{(procedure.OperationName is null ? "" : $" in '{procedure.OperationName}'")}");
                var ucsNames = api.GetUcsNames();
                Console.WriteLine("UCS: " + (ucsNames.Count == 0 ? "(none read)" : string.Join(", ", ucsNames)));
                foreach (var name in ucsNames.Take(8))
                {
                    var ucs = api.GetUcs(name);
                    Console.WriteLine($"  UCS '{name}' matrix: {(ucs is null ? "not readable" : string.Join(", ", ucs.Matrix.Select(value => value.ToString("G6", System.Globalization.CultureInfo.InvariantCulture))))}");
                }

                var processXml = Path.Combine(AppContext.BaseDirectory, "process-manager.xml");
                api.ExportProcessManagerXml(processXml);
                Console.WriteLine(File.Exists(processXml) ? $"Process manager XML: {processXml}" : "Process manager XML: Cimatron wrote no file");
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

// Whether Windows finds Cimatron's type libraries (calls into Cimatron fail with "Library not registered" without them).
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
static void WriteRegistrationStatus(ExportOptions options)
{
    var folder = CimatronTypeLibrary.FindProgramFolder(options.CimatronFolder);
    Console.WriteLine("Cimatron type libraries:");
    if (folder is null)
    {
        Console.WriteLine("  (no Cimatron Program folder found)");
        return;
    }

    foreach (var library in CimatronTypeLibrary.ProbeLibraries)
        Console.WriteLine($"  {library}: {CimatronTypeLibrary.RegistrationStatus(Path.Combine(folder, library))}");
    var classes = CimatronTypeLibrary.Classes(folder).ToList();
    var missing = classes.Where(entry => !entry.Registered).ToList();
    Console.WriteLine($"Cimatron COM classes registered: {classes.Count - missing.Count} of {classes.Count}");
    foreach (var (library, name, id, _) in missing.Take(80)) Console.WriteLine($"  not registered: {library} {name} {id:B}");
    var binder = CimatronComApi.Binder;
    Console.WriteLine($"Cimatron interop interfaces usable without IDispatch: {binder?.InterfaceCount ?? 0}");
    foreach (var problem in CimatronComApi.BinderProblems) Console.WriteLine("  " + problem);
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
        var libraries = CimatronTypeLibrary.ProbeLibraries.Select(library => Path.Combine(folder, library))
            .Concat(Directory.EnumerateFiles(folder, "*.tlb").Order(StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var library in libraries) lines.AddRange(CimatronTypeLibrary.Describe(library));
        File.WriteAllLines(target, lines);
        Console.WriteLine($"Cimatron API listing ({lines.Count} lines): {target}");
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
    {
        Console.WriteLine($"Cimatron API listing failed: {exception.Message}");
    }
}
