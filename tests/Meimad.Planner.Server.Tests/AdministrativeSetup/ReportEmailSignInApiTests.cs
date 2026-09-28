using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Meimad.Planner.Server.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Meimad.Planner.Server.Tests.AdministrativeSetup;

/// <summary>Mailbox sign-in for report email (schema v89) and the test email.</summary>
public sealed class ReportEmailSignInApiTests
{
    private const string Password = "abcd efgh ijkl mnop";

    [Fact]
    public async Task The_password_is_stored_encrypted_never_returned_and_kept_until_replaced_or_removed()
    {
        await RunAsync(async (application, client) =>
        {
            using var saved = await PutAsync(client, Settings(587, userName: "planner@example.com", password: Password));
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            var body = await saved.Content.ReadAsStringAsync();
            Assert.DoesNotContain(Password, body, StringComparison.Ordinal);
            using (var json = JsonDocument.Parse(body))
            {
                Assert.Equal("planner@example.com", json.RootElement.GetProperty("smtpUserName").GetString());
                Assert.True(json.RootElement.GetProperty("smtpPasswordConfigured").GetBoolean());
            }
            var stored = await StoredPasswordAsync(application);
            Assert.NotNull(stored);
            Assert.DoesNotContain("abcd", stored, StringComparison.Ordinal);

            // A client that sends no sign-in fields keeps both.
            using var older = await PutAsync(client, Settings(587));
            Assert.Equal(HttpStatusCode.OK, older.StatusCode);
            using (var json = JsonDocument.Parse(await older.Content.ReadAsStringAsync()))
            {
                Assert.Equal("planner@example.com", json.RootElement.GetProperty("smtpUserName").GetString());
                Assert.True(json.RootElement.GetProperty("smtpPasswordConfigured").GetBoolean());
            }
            Assert.Equal(stored, await StoredPasswordAsync(application));

            // The user name cannot be removed while a password is saved; both can be removed together.
            using var noUser = await PutAsync(client, Settings(587, userName: ""));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, noUser.StatusCode);
            using var cleared = await PutAsync(client, Settings(587, userName: "", clearPassword: true));
            Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
            using (var json = JsonDocument.Parse(await cleared.Content.ReadAsStringAsync()))
            {
                Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("smtpUserName").ValueKind);
                Assert.False(json.RootElement.GetProperty("smtpPasswordConfigured").GetBoolean());
            }
            Assert.Null(await StoredPasswordAsync(application));

            using var implicitTls = await PutAsync(client, Settings(465));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, implicitTls.StatusCode);
            Assert.Contains("587", await implicitTls.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            using var passwordWithoutUser = await PutAsync(client, Settings(587, userName: "", password: Password));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, passwordWithoutUser.StatusCode);
        });
    }

    [Fact]
    public async Task The_test_email_signs_in_with_the_saved_password_and_explains_a_refused_sign_in()
    {
        await using var smtp = new FakeSmtpServer(rejectSignIn: false);
        await RunAsync(async (_, client) =>
        {
            using (client.SignedInWithOnly())
            {
                using var forbidden = await client.PostAsync("/api/v1/report-email-settings/test", null);
                Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            }
            using var notConfigured = await client.PostAsync("/api/v1/report-email-settings/test", null);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, notConfigured.StatusCode);

            using var saved = await PutAsync(client, Settings(smtp.Port, userName: "planner@example.com", password: Password, useSsl: false));
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            using var sent = await client.PostAsync("/api/v1/report-email-settings/test", null);
            Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
            using var json = JsonDocument.Parse(await sent.Content.ReadAsStringAsync());
            Assert.Equal(["manager@example.com"], json.RootElement.GetProperty("sentTo").EnumerateArray().Select(value => value.GetString()));
            Assert.True(json.RootElement.GetProperty("signedIn").GetBoolean());
            await smtp.Finished;
            Assert.Equal(("planner@example.com", Password), (smtp.UserName, smtp.Password));
            Assert.Contains("manager@example.com", Assert.Single(smtp.Recipients), StringComparison.Ordinal);
            Assert.Contains("Meimad Planner test email", smtp.Data, StringComparison.Ordinal);
        });

        await using var refusing = new FakeSmtpServer(rejectSignIn: true);
        await RunAsync(async (_, client) =>
        {
            using var saved = await PutAsync(client, Settings(refusing.Port, userName: "planner@example.com", password: "wrong", useSsl: false));
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            using var failed = await client.PostAsync("/api/v1/report-email-settings/test", null);
            Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
            var body = await failed.Content.ReadAsStringAsync();
            Assert.Contains("report_delivery_failed", body, StringComparison.Ordinal);
            Assert.Contains("5.7.", body, StringComparison.Ordinal);   // the mail server's own reply
        });
    }

    private static object Settings(
        int port, string? userName = null, string? password = null, bool clearPassword = false, bool useSsl = true) => new
    {
        senderAddress = "planner@example.com",
        recipients = new[] { "manager@example.com" },
        smtpHost = "127.0.0.1",
        smtpPort = port,
        useSsl,
        dailyReportEnabled = false,
        dailyReportTimeLocal = (string?)null,
        timeZoneId = "Asia/Jerusalem",
        weeklyMaterialReportEnabled = false,
        weeklyMaterialReportSendDay = "thursday",
        weeklyMaterialReportTimeLocal = "08:00",
        weeklyEmployeeEfficiencyEnabled = false,
        weeklyEmployeeEfficiencySendDay = "sunday",
        weeklyEmployeeEfficiencyTimeLocal = "08:00",
        smtpUserName = userName,
        smtpPassword = password,
        clearSmtpPassword = clearPassword
    };

    private static async Task<HttpResponseMessage> PutAsync(HttpClient client, object settings)
    {
        using var current = await client.GetAsync("/api/v1/report-email-settings");
        current.EnsureSuccessStatusCode();
        using var put = new HttpRequestMessage(HttpMethod.Put, "/api/v1/report-email-settings")
        {
            Content = JsonContent.Create(settings)
        };
        put.Headers.IfMatch.Add(new EntityTagHeaderValue(current.Headers.ETag!.Tag));
        return await client.SendAsync(put);
    }

    private static async Task<string?> StoredPasswordAsync(WebApplication application)
    {
        var database = application.Services.GetRequiredService<SqliteDatabase>();
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT smtp_password_protected FROM report_email_settings WHERE id = 1;";
        return await command.ExecuteScalarAsync() as string;
    }

    private static async Task RunAsync(Func<WebApplication, HttpClient, Task> test)
    {
        var folder = Path.Combine(Path.GetTempPath(), "MeimadPlanner.ReportEmail.Tests", Guid.NewGuid().ToString("N"));
        var application = ServerApplication.Build(
            [$"--Database:Path={Path.Combine(folder, "test.db")}"],
            host =>
            {
                host.UseSignedInTestServer();
                host.ConfigureServices(services =>
                {
                    services.RemoveAll<IDataProtectionProvider>();
                    services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
                });
            });
        try
        {
            await application.StartAsync();
            using var client = application.GetTestClient();
            await test(application, client);
            await application.StopAsync();
        }
        finally
        {
            await application.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    /// <summary>
    /// A one-connection SMTP server on loopback that offers AUTH LOGIN and records the sign-in and the
    /// message. Refusing, it answers the sign-in with 535 and MAIL FROM with 530, as Gmail does.
    /// </summary>
    private sealed class FakeSmtpServer : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly bool rejectSignIn;

        internal FakeSmtpServer(bool rejectSignIn)
        {
            this.rejectSignIn = rejectSignIn;
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Finished = ServeAsync();
        }

        internal int Port { get; }
        internal Task Finished { get; }
        internal string? UserName { get; private set; }
        internal string? Password { get; private set; }
        internal List<string> Recipients { get; } = [];
        internal string Data { get; private set; } = string.Empty;

        private async Task ServeAsync()
        {
            using var connection = await listener.AcceptTcpClientAsync();
            await using var stream = connection.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII);
            await using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
            await writer.WriteLineAsync("220 fake.smtp ESMTP");
            var signedIn = false;
            while (await reader.ReadLineAsync() is { } line)
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                switch (parts[0].ToUpperInvariant())
                {
                    case "EHLO":
                        await writer.WriteAsync("250-fake.smtp\r\n250 AUTH LOGIN\r\n");
                        break;
                    case "AUTH":
                        var user = parts.Length > 2 ? parts[2] : await PromptAsync(writer, reader, "334 VXNlcm5hbWU6");
                        var password = await PromptAsync(writer, reader, "334 UGFzc3dvcmQ6");
                        UserName = Decode(user);
                        Password = Decode(password);
                        signedIn = !rejectSignIn;
                        await writer.WriteLineAsync(signedIn ? "235 2.7.0 Accepted" : "535 5.7.8 Username and Password not accepted");
                        break;
                    case "MAIL":
                        await writer.WriteLineAsync(signedIn ? "250 2.1.0 OK" : "530 5.7.0 Authentication Required");
                        break;
                    case "RCPT":
                        Recipients.Add(line);
                        await writer.WriteLineAsync("250 2.1.5 OK");
                        break;
                    case "DATA":
                        await writer.WriteLineAsync("354 Go ahead");
                        var data = new StringBuilder();
                        while (await reader.ReadLineAsync() is { } dataLine && dataLine != ".") data.AppendLine(dataLine);
                        Data = data.ToString();
                        await writer.WriteLineAsync("250 2.0.0 OK");
                        break;
                    case "QUIT":
                        await writer.WriteLineAsync("221 2.0.0 Bye");
                        return;
                    default:
                        await writer.WriteLineAsync("250 OK");
                        break;
                }
            }
        }

        private static async Task<string> PromptAsync(StreamWriter writer, StreamReader reader, string prompt)
        {
            await writer.WriteLineAsync(prompt);
            return await reader.ReadLineAsync() ?? string.Empty;
        }

        private static string Decode(string value) => Encoding.UTF8.GetString(Convert.FromBase64String(value));

        public async ValueTask DisposeAsync()
        {
            listener.Stop();
            try { await Finished.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException or IOException or TimeoutException) { }
        }
    }
}
