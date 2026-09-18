using System.Diagnostics;
using System.IO;
using Meimad.Planner.Client.Windows.Api;

namespace Meimad.Planner.Client.Windows.Presentation;

internal enum ClientUpdateAction
{
    /// <summary>The client and the Server's client package are the same version.</summary>
    None,

    /// <summary>The Server distributes a newer client; it is downloaded and installed.</summary>
    Install,

    /// <summary>This client is newer than the Server's client package; the Server needs the upgrade.</summary>
    ClientNewerThanServer,

    /// <summary>The versions differ but the Server has no client installer to distribute.</summary>
    InstallerMissing
}

internal sealed record ClientUpdateDecision(
    ClientUpdateAction Action,
    string RunningVersion,
    string? ServerClientVersion,
    string Message);

internal sealed class ClientUpdateAvailableEventArgs(
    ClientUpdateDecision decision,
    ClientInstallerManifest manifest,
    Func<string, IProgress<long>?, CancellationToken, Task<ClientInstallerDownload>> downloadAsync) : EventArgs
{
    public ClientUpdateDecision Decision { get; } = decision;

    public ClientInstallerManifest Manifest { get; } = manifest;

    /// <summary>Downloads the installer into the given folder and verifies its checksum.</summary>
    public Func<string, IProgress<long>?, CancellationToken, Task<ClientInstallerDownload>> DownloadAsync { get; } = downloadAsync;
}

/// <summary>
/// Decides whether the running client fits the Server: the Server ships the client MSI that
/// belongs to its own version, and a client whose version differs installs it.
/// </summary>
internal static class ClientUpdatePolicy
{
    internal static Version RunningVersion { get; } =
        Normalize(typeof(ClientUpdatePolicy).Assembly.GetName().Version ?? new Version(0, 0, 0));

    internal static string Format(Version version) =>
        $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";

    /// <summary>Parses "0.1.117", "0.1.117+build", or "0.1.117.0" to a major.minor.patch version.</summary>
    internal static bool TryParse(string? value, out Version version)
    {
        version = new Version(0, 0, 0);
        var core = (value ?? string.Empty).Split('+', '-')[0].Trim();
        if (!Version.TryParse(core, out var parsed))
        {
            return false;
        }

        version = Normalize(parsed);
        return true;
    }

    internal static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(version.Build, 0));

    internal static ClientUpdateDecision Evaluate(Version running, ClientInstallerManifest manifest)
    {
        running = Normalize(running);
        var runningText = Format(running);
        if (!manifest.InstallerAvailable || !TryParse(manifest.ClientVersion, out var offered))
        {
            if (TryParse(manifest.ServerVersion, out var server) && server != running)
            {
                return new ClientUpdateDecision(
                    ClientUpdateAction.InstallerMissing,
                    runningText,
                    null,
                    $"Server {Format(server)} expects client {Format(server)}, but this client is {runningText} "
                    + "and the Server has no client installer to distribute. Ask the administrator to install "
                    + $"client {Format(server)}.");
            }

            return new ClientUpdateDecision(ClientUpdateAction.None, runningText, null, string.Empty);
        }

        var offeredText = Format(offered);
        if (offered == running)
        {
            return new ClientUpdateDecision(ClientUpdateAction.None, runningText, offeredText, string.Empty);
        }

        if (offered > running)
        {
            return new ClientUpdateDecision(
                ClientUpdateAction.Install,
                runningText,
                offeredText,
                $"New version {offeredText} is available (this client is {runningText}). "
                + "The client will be installed now.");
        }

        return new ClientUpdateDecision(
            ClientUpdateAction.ClientNewerThanServer,
            runningText,
            offeredText,
            $"This client ({runningText}) is newer than the client package on the Server ({offeredText}). "
            + $"Upgrade the Server, or install client {offeredText} on this PC.");
    }
}

/// <summary>
/// Runs the downloaded MSI after the client has exited. Windows Installer cannot replace the
/// files of a running client, so a small command script waits a few seconds, installs with
/// the passive progress UI (the UAC prompt still appears for a per-machine install), and
/// starts the new client again.
/// </summary>
internal static class ClientInstallerLauncher
{
    internal const string ScriptFileName = "install-client-update.cmd";

    internal const string LogFileName = "install-client-update.log";

    internal static string BuildScript(string installerPath, string logPath, string? relaunchExecutablePath)
    {
        var lines = new List<string>
        {
            "@echo off",
            "rem Meimad Planner client update: wait for the client to exit, install the new version, restart it.",
            "ping -n 4 127.0.0.1 >nul",
            $"msiexec.exe /i \"{installerPath}\" /passive /norestart /l*v \"{logPath}\""
        };
        if (!string.IsNullOrWhiteSpace(relaunchExecutablePath))
        {
            lines.Add($"if not errorlevel 1 start \"\" \"{relaunchExecutablePath}\"");
        }

        return string.Join("\r\n", lines) + "\r\n";
    }

    /// <summary>Writes the script next to the installer and starts it detached; returns the script path.</summary>
    internal static string Launch(string installerPath, string? relaunchExecutablePath)
    {
        var folder = Path.GetDirectoryName(installerPath) ?? Path.GetTempPath();
        var scriptPath = Path.Combine(folder, ScriptFileName);
        var logPath = Path.Combine(folder, LogFileName);
        File.WriteAllText(scriptPath, BuildScript(installerPath, logPath, relaunchExecutablePath));
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"\"{scriptPath}\"\"")
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = folder
        });
        return scriptPath;
    }
}
