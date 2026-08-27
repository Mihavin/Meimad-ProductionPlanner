using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

return await CncSimulator.RunAsync(args);

internal static class CncSimulator
{
    private static readonly IReadOnlyDictionary<string, string> EventCodes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["OFFSET_LOADER_COMPLETED"] = "OLC",
            ["SETUP_VERIFICATION_REQUESTED"] = "SVR",
            ["SETUP_VERIFICATION_SUCCEEDED"] = "SVS",
            ["SETUP_VERIFICATION_FAILED"] = "SVF",
            ["SEND_TO_QC"] = "STQ",
            ["QC_PASS"] = "QCP",
            ["QC_FAIL"] = "QCF",
            ["CYCLE_START"] = "CST",
            ["CYCLE_END"] = "CEN",
            ["CYCLE_INTERRUPTED"] = "CIN",
            ["PRODUCTION_SESSION_OPENED"] = "PSO",
            ["PRODUCTION_SESSION_CLOSED"] = "PSC"
        };

    internal static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var options = SimulatorOptions.Parse(args);
            var json = await File.ReadAllTextAsync(options.ScenarioPath);
            var scenario = JsonSerializer.Deserialize<SimulatorScenario>(json,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new InvalidOperationException("Scenario JSON is empty.");
            RequiredMachineId(scenario.MachineId);
            var lines = scenario.Events.Select(BuildLine).ToArray();
            if (options.OutputPath is not null)
            {
                if (File.Exists(options.OutputPath) && !options.Force)
                    throw new InvalidOperationException(
                        $"Refusing to overwrite '{options.OutputPath}'. Use --force after reviewing the target.");
                var parent = Path.GetDirectoryName(options.OutputPath);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                var deliveredLines = scenario.Events.Zip(lines)
                    .SelectMany(pair => Enumerable.Repeat(pair.Second, pair.First.Repeat))
                    .ToArray();
                await File.WriteAllTextAsync(options.OutputPath,
                    string.Join("\r\n", deliveredLines) + "\r\n", Encoding.ASCII);
                Console.WriteLine($"Wrote {deliveredLines.Length} strict Machine-output lines to {options.OutputPath}.");
                return 0;
            }
            if (options.ValidateOnly)
            {
                Console.WriteLine($"Development Machine: {scenario.MachineId}");
                foreach (var line in lines) Console.WriteLine(line);
                Console.WriteLine($"Validated {lines.Length} simulator events.");
                return 0;
            }

            var address = IPAddress.Parse(options.BindAddress);
            var listener = new TcpListener(address, options.Port);
            listener.Start();
            Console.WriteLine(
                $"Development-only Haas DPRNT simulator for Machine {scenario.MachineId} listening on {address}:{options.Port}.");
            Console.WriteLine("Configure that development Machine's DPRNT connection to this listener.");
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            var stopwatch = Stopwatch.StartNew();
            foreach (var pair in scenario.Events.Zip(lines))
            {
                if (pair.First.AtMs is < 0)
                    throw new InvalidOperationException("atMs must be nonnegative when supplied.");
                var scheduledDelay = pair.First.AtMs.HasValue
                    ? Math.Max(0, pair.First.AtMs.Value - stopwatch.ElapsedMilliseconds)
                    : 0;
                var totalDelay = checked(scheduledDelay + pair.First.DelayMs);
                if (totalDelay > 0) await Task.Delay(TimeSpan.FromMilliseconds(totalDelay));
                var bytes = Encoding.ASCII.GetBytes(pair.Second + "\r\n");
                var repeat = pair.First.Repeat;
                for (var index = 0; index < repeat; index++)
                {
                    await stream.WriteAsync(bytes);
                    await stream.FlushAsync();
                    Console.WriteLine($"> {pair.Second}");
                }
            }
            listener.Stop();
            Console.WriteLine("Scenario complete.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    internal static string BuildLine(SimulatorEvent value)
    {
        if (!EventCodes.TryGetValue(value.EventType ?? string.Empty, out var code))
            throw new InvalidOperationException($"Unsupported eventType '{value.EventType}'.");
        RequiredIdentity(value.EventId, "eventId");
        if (value.Sequence < 0) throw new InvalidOperationException("sequence must be nonnegative.");
        if (value.MacroVersion <= 0) throw new InvalidOperationException("macroVersion must be positive.");
        if (value.AtMs is < 0 or > 86_400_000)
            throw new InvalidOperationException("atMs must be between 0 and 86400000.");
        if (value.DelayMs is < 0 or > 600_000)
            throw new InvalidOperationException("delayMs must be between 0 and 600000.");
        if (value.Repeat is < 1 or > 20)
            throw new InvalidOperationException("repeat must be between 1 and 20.");
        OptionalIdentity(value.ProductionRunId, "productionRunId");
        OptionalIdentity(value.ProgramIdentity, "programIdentity");
        var builder = new StringBuilder("MEIMAD/V/1/EVENT/")
            .Append(code).Append("/ID/").Append(value.EventId)
            .Append("/SEQ/").Append(value.Sequence.ToString(CultureInfo.InvariantCulture))
            .Append("/MACROVERSION/").Append(value.MacroVersion.ToString(CultureInfo.InvariantCulture));
        if (value.ProductionRunId is not null)
            builder.Append("/RUN/").Append(value.ProductionRunId);
        if (value.ProgramIdentity is not null)
            builder.Append("/PROGRAM/").Append(value.ProgramIdentity);
        if (code == "OLC")
        {
            if (value.OffsetRelease is < 100000 or > 999999
                || value.Nonce is < 100000 or > 999999)
                throw new InvalidOperationException(
                    "OFFSET_LOADER_COMPLETED requires six-digit offsetRelease and nonce.");
            builder.Append("/OFFSETRELEASE/").Append(
                    value.OffsetRelease.GetValueOrDefault().ToString(CultureInfo.InvariantCulture))
                .Append("/NONCE/").Append(
                    value.Nonce.GetValueOrDefault().ToString(CultureInfo.InvariantCulture));
        }
        else if (value.OffsetRelease.HasValue || value.Nonce.HasValue)
        {
            throw new InvalidOperationException(
                "offsetRelease and nonce are allowed only for OFFSET_LOADER_COMPLETED.");
        }
        var line = builder.ToString();
        if (Encoding.ASCII.GetByteCount(line) > 512)
            throw new InvalidOperationException("Generated line exceeds the 512-byte protocol limit.");
        return line;
    }

    private static void OptionalIdentity(string? value, string name)
    {
        if (value is not null) RequiredIdentity(value, name);
    }

    private static void RequiredMachineId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 200
            || value.Any(char.IsControl))
            throw new InvalidOperationException(
                "machineId is required and must be a printable value up to 200 characters.");
    }

    private static void RequiredIdentity(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128
            || !value.All(character => character is >= 'A' and <= 'Z'
                or >= '0' and <= '9' or '-'))
            throw new InvalidOperationException(
                $"{name} must use 1-128 uppercase letters, digits, or hyphens.");
    }
}

internal sealed record SimulatorScenario(string? MachineId, IReadOnlyList<SimulatorEvent> Events);

internal sealed record SimulatorEvent(
    string? EventType,
    string? EventId,
    long Sequence,
    int MacroVersion = 3,
    string? ProductionRunId = null,
    string? ProgramIdentity = null,
    int? OffsetRelease = null,
    int? Nonce = null,
    long? AtMs = null,
    int DelayMs = 0,
    int Repeat = 1);

internal sealed record SimulatorOptions(
    string BindAddress,
    int Port,
    string ScenarioPath,
    bool ValidateOnly,
    string? OutputPath,
    bool Force)
{
    internal static SimulatorOptions Parse(string[] args)
    {
        string? scenario = null;
        var bind = "127.0.0.1";
        var port = 8080;
        var validateOnly = false;
        string? output = null;
        var force = false;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--scenario" when index + 1 < args.Length:
                    scenario = args[++index];
                    break;
                case "--bind" when index + 1 < args.Length:
                    bind = args[++index];
                    break;
                case "--port" when index + 1 < args.Length
                    && int.TryParse(args[++index], out var parsedPort):
                    port = parsedPort;
                    break;
                case "--validate-only":
                    validateOnly = true;
                    break;
                case "--output" when index + 1 < args.Length:
                    output = args[++index];
                    break;
                case "--force":
                    force = true;
                    break;
                default:
                    throw new InvalidOperationException(
                        "Usage: --scenario <file.json> [--bind 127.0.0.1] [--port 8080] [--validate-only | --output <transcript.txt> [--force]]");
            }
        }
        if (scenario is null || !File.Exists(scenario))
            throw new InvalidOperationException("A readable --scenario JSON file is required.");
        if (port is < 1 or > 65535) throw new InvalidOperationException("port must be 1-65535.");
        if (!IPAddress.TryParse(bind, out _)) throw new InvalidOperationException("bind must be an IP address.");
        if (validateOnly && output is not null)
            throw new InvalidOperationException("--validate-only and --output are mutually exclusive.");
        if (force && output is null)
            throw new InvalidOperationException("--force is valid only with --output.");
        return new(bind, port, Path.GetFullPath(scenario), validateOnly,
            output is null ? null : Path.GetFullPath(output), force);
    }
}
