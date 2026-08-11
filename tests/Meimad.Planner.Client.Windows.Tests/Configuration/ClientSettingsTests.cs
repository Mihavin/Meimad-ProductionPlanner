using Meimad.Planner.Client.Windows.Configuration;

namespace Meimad.Planner.Client.Windows.Tests.Configuration;

public sealed class ClientSettingsTests
{
    [Fact]
    public async Task First_load_persists_a_stable_generated_client_id()
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "client-settings.json");
        try
        {
            var store = new ClientSettingsStore(path);

            var first = await store.LoadAsync();
            var second = await store.LoadAsync();

            Assert.True(File.Exists(path));
            Assert.StartsWith("windows-", first.ClientId, StringComparison.Ordinal);
            Assert.Equal(first.ClientId, second.ClientId);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Settings_round_trip_server_user_and_stable_client_id()
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "client-settings.json");
        try
        {
            var store = new ClientSettingsStore(path);
            var expected = ClientSettings.Create(
                "http://planner-server:5080",
                "Miriam Planner",
                "windows-client-01");

            await store.SaveAsync(expected);
            var loaded = await store.LoadAsync();

            Assert.Equal(new Uri("http://planner-server:5080/"), loaded.ServerBaseUri);
            Assert.Equal("Miriam Planner", loaded.LocalUserName);
            Assert.Equal("windows-client-01", loaded.ClientId);
            var content = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain("Database", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("SQLite", content, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("file:///C:/planner.db")]
    [InlineData("http://user:password@planner-server:5080/")]
    [InlineData("http://planner-server:5080/api/")]
    [InlineData("planner-server:5080")]
    public void Settings_reject_non_server_root_addresses(string address)
    {
        Assert.Throws<ClientSettingsException>(() => ClientSettings.Create(
            address,
            "Planner",
            "client-1"));
    }

    [Fact]
    public void Unicode_display_name_produces_stable_ascii_api_user_id()
    {
        var first = ClientSettings.Create(
            "http://planner-server:5080/",
            "מתכנן ייצור",
            "client-1");
        var second = ClientSettings.Create(
            "http://planner-server:5080/",
            "מתכנן ייצור",
            "client-2");

        Assert.Equal(first.LocalUserId, second.LocalUserId);
        Assert.Matches("^local-[0-9a-f]{24}$", first.LocalUserId);
    }

    [Fact]
    public async Task Invalid_local_settings_fail_safely()
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "client-settings.json");
        try
        {
            await File.WriteAllTextAsync(path, "{not-json");
            var store = new ClientSettingsStore(path);

            var exception = await Assert.ThrowsAsync<ClientSettingsException>(() =>
                store.LoadAsync());

            Assert.Contains("could not be read", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "MeimadPlanner.Client.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
