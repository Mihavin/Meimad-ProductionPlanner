using Meimad.CimatronExporter;

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
    api = CimatronComApi.Connect(options.ProgId ?? Environment.GetEnvironmentVariable("MEIMAD_CIMATRON_PROGID"));
}
catch (CimatronApiException exception)
{
    Console.Error.WriteLine($"{exception.Code}: {exception.Message}");
    if (options.Probe && OperatingSystem.IsWindows())
    {
        // The probe's output goes to the console and a redirected file alike.
        Console.WriteLine($"{exception.Code}: {exception.Message}");
        foreach (var line in CimatronDiscovery.Report()) Console.WriteLine(line);
    }
    return 3;
}

using (api)
{
    if (options.Probe)
    {
        Console.WriteLine($"Cimatron {api.CimatronVersion ?? "(version unknown)"}, adapter {api.AdapterVersion}");
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
