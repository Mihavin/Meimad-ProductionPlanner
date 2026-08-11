using System.IO;
using System.Text.Json;

namespace Meimad.Planner.Client.Windows.Configuration;

internal interface IClientSettingsStore
{
    Task<ClientSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(ClientSettings settings, CancellationToken cancellationToken = default);
}

internal sealed class ClientSettingsStore : IClientSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly string settingsPath;

    internal ClientSettingsStore(string? settingsPath = null)
    {
        this.settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Meimad Planner",
            "client-settings.json");
    }

    public async Task<ClientSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(settingsPath))
        {
            var defaults = ClientSettings.Default();
            await SaveAsync(defaults, cancellationToken);
            return defaults;
        }

        try
        {
            await using var stream = new FileStream(
                settingsPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var stored = await JsonSerializer.DeserializeAsync<StoredClientSettings>(
                stream,
                JsonOptions,
                cancellationToken);
            return ClientSettings.Create(
                stored?.ServerAddress,
                stored?.LocalUserName,
                stored?.ClientId);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or ClientSettingsException)
        {
            throw new ClientSettingsException(
                "The local client settings file could not be read. Correct or remove it and try again.");
        }
    }

    public async Task SaveAsync(
        ClientSettings settings,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(settingsPath)
            ?? throw new InvalidOperationException("The client settings path has no parent directory.");
        Directory.CreateDirectory(directory);
        var pendingPath = settingsPath + ".pending";
        try
        {
            await using (var stream = new FileStream(
                pendingPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    new StoredClientSettings(
                        settings.ServerBaseUri.AbsoluteUri,
                        settings.LocalUserName,
                        settings.ClientId),
                    JsonOptions,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(pendingPath, settingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(pendingPath))
            {
                File.Delete(pendingPath);
            }
        }
    }

    private sealed record StoredClientSettings(
        string? ServerAddress,
        string? LocalUserName,
        string? ClientId);
}
