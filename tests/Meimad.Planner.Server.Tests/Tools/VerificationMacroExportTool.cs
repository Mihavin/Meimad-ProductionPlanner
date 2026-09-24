using System.Globalization;
using System.Text;
using System.Text.Json;
using Meimad.Planner.Server.Application.GCode;

namespace Meimad.Planner.Server.Tests.Tools;

/// <summary>
/// An opt-in tool, not a check of the build: with <c>MEIMAD_EXPORT_MACROS_TO=&lt;folder&gt;</c> set it
/// reads every Machine and its verification configuration from the live Server (read-only GETs,
/// <c>MEIMAD_SERVER_URI</c>, default http://127.0.0.1:5080), renders the protected subprograms
/// with the same generator the Server's <c>verification-macros</c> endpoint uses, and writes one
/// zip per CNC Machine plus an index. Without the variable it does nothing.
/// </summary>
public sealed class VerificationMacroExportTool
{
    [Fact]
    public async Task Export_the_macro_packages_of_every_live_cnc_machine_when_requested()
    {
        var target = Environment.GetEnvironmentVariable("MEIMAD_EXPORT_MACROS_TO");
        if (string.IsNullOrWhiteSpace(target)) return;
        var baseUri = Environment.GetEnvironmentVariable("MEIMAD_SERVER_URI") ?? "http://127.0.0.1:5080";
        Directory.CreateDirectory(target);
        using var client = new HttpClient { BaseAddress = new Uri(baseUri), Timeout = TimeSpan.FromSeconds(30) };
        using var machinesDocument = JsonDocument.Parse(await client.GetStringAsync("/api/v1/machines"));
        var machines = machinesDocument.RootElement.ValueKind == JsonValueKind.Array
            ? machinesDocument.RootElement.EnumerateArray().ToArray()
            : machinesDocument.RootElement.GetProperty("items").EnumerateArray().ToArray();
        var index = new StringBuilder("MEIMAD VERIFICATION MACRO PACKAGES\r\n\r\n");
        foreach (var machine in machines)
        {
            if (machine.GetProperty("executionMode").GetString() != "CNC_GCODE") continue;
            var machineId = machine.GetProperty("machineId").GetString()!;
            var number = machine.GetProperty("number").GetString() ?? machineId;
            var name = machine.GetProperty("name").GetString() ?? string.Empty;
            var dialect = NcDialects.Profile(machine.GetProperty("ncDialect").GetString());
            var settings = NcVerificationMacroGenerator.DefaultSettings(dialect);
            using var configuration = await client.GetAsync($"/api/v1/machines/{machineId}/verification-configuration");
            if (configuration.IsSuccessStatusCode)
            {
                using var stored = JsonDocument.Parse(await configuration.Content.ReadAsStringAsync());
                var value = stored.RootElement;
                settings = new NcVerificationMacroSettings(
                    value.GetProperty("challengeProgramNumber").GetInt32(),
                    value.GetProperty("verifyProgramNumber").GetInt32(),
                    Optional(value, "finalizeProgramNumber") ?? settings.FinalizeProgramNumber,
                    value.GetProperty("nonceVariable").GetInt32(),
                    value.GetProperty("responseVariable").GetInt32(),
                    value.GetProperty("verificationStateVariable").GetInt32(),
                    value.GetProperty("releaseTokenVariable").GetInt32(),
                    Optional(value, "eventSequenceVariable") ?? dialect.DefaultEventSequenceVariable,
                    value.GetProperty("expectedMacroVersion").GetInt32(),
                    value.GetProperty("responseCodeDigits").GetInt32(),
                    value.GetProperty("verificationTimeoutSeconds").GetInt32(),
                    FromConfiguration: true);
            }
            var package = NcVerificationMacroGenerator.Generate(dialect, settings, number, name);
            var safeName = new string(name.Where(character => char.IsAsciiLetterOrDigit(character) || character is '-' or ' ').ToArray()).Trim().Replace(' ', '-');
            var fileName = string.Create(CultureInfo.InvariantCulture,
                $"{number}-{safeName}-{dialect.Id}-v{settings.MacroVersion}.zip");
            await File.WriteAllBytesAsync(Path.Combine(target, fileName), NcVerificationMacroZip.Build(package));
            index.Append(CultureInfo.InvariantCulture,
                $"{fileName}: {(settings.FromConfiguration ? "configured variables" : "DIALECT DEFAULTS - configure in Setup before enabling verification")}; files: {string.Join(", ", package.Files.Select(file => file.FileName))}\r\n");
        }
        await File.WriteAllTextAsync(Path.Combine(target, "INDEX.txt"), index.ToString());
    }

    private static int? Optional(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number ? property.GetInt32() : null;
}
