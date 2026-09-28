using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using Meimad.Planner.Server.Application.AdministrativeSetup;
using Meimad.Planner.Server.Domain.AdministrativeSetup;
using Microsoft.AspNetCore.DataProtection;

namespace Meimad.Planner.Server.Application.Reports;

/// <summary>
/// The mail server connection of report email (schema v89, owner decision 2026-09-28). The mailbox
/// password is encrypted with the Server's Data Protection keys, so only this Server can read it and
/// a database copied to another machine needs the password entered again. Clients never receive it.
/// </summary>
internal sealed class ReportEmailSmtp
{
    private readonly IDataProtector passwordProtector;

    public ReportEmailSmtp(IDataProtectionProvider dataProtectionProvider) =>
        passwordProtector = dataProtectionProvider.CreateProtector("Meimad.Planner.ReportEmail.SmtpPassword.v1");

    internal string Protect(string password) => passwordProtector.Protect(password);

    /// <summary>An SMTP client for the saved settings, signed in when a user name and password are saved.</summary>
    internal SmtpClient CreateClient(ReportEmailSettings settings)
    {
        var credentials = settings.SignsIn
            ? new NetworkCredential(settings.SmtpUserName, Unprotect(settings.ProtectedSmtpPassword!))
            : null;
        return new SmtpClient(settings.SmtpHost!, settings.SmtpPort!.Value)
        {
            EnableSsl = settings.UseSsl,
            UseDefaultCredentials = false,
            Credentials = credentials
        };
    }

    /// <summary>The failure with its causes, for example the mail server's reply to the sign-in.</summary>
    internal static string Describe(Exception exception)
    {
        var messages = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            var message = current.Message.Trim();
            if (message.Length > 0 && !messages.Contains(message, StringComparer.Ordinal)) messages.Add(message);
        }
        var text = string.Join(" ", messages);
        return text.Length <= 1000 ? text : text[..1000];
    }

    private string Unprotect(string protectedPassword)
    {
        try
        {
            return passwordProtector.Unprotect(protectedPassword);
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            throw new SmtpException(
                "The saved email password cannot be decrypted on this Server machine. Enter it again in Setup, Reports / Email, and save.");
        }
    }
}

internal sealed record ReportEmailTestResult(IReadOnlyList<string> SentTo, DateTimeOffset SentAt, bool SignedIn);

internal sealed class ReportEmailNotConfiguredException(string message) : Exception(message);

/// <summary>Sends a short test email with the saved report email settings.</summary>
internal sealed class ReportEmailTestService(
    AdministrativeSetupService setup, ReportEmailSmtp smtp, TimeProvider timeProvider)
{
    internal async Task<ReportEmailTestResult> SendAsync(CancellationToken token = default)
    {
        var settings = await setup.GetReportEmailSettingsAsync(token);
        if (string.IsNullOrWhiteSpace(settings.SenderAddress)
            || string.IsNullOrWhiteSpace(settings.SmtpHost)
            || settings.SmtpPort is null)
        {
            throw new ReportEmailNotConfiguredException(
                "Save the sender address, SMTP host and SMTP port before sending a test email.");
        }

        // Without recipients the test goes to the sender's own mailbox.
        IReadOnlyList<string> recipients = settings.Recipients.Count > 0 ? settings.Recipients : [settings.SenderAddress];
        var sentAt = timeProvider.GetUtcNow();
        using var message = new MailMessage
        {
            From = new MailAddress(settings.SenderAddress),
            Subject = "Meimad Planner test email",
            Body = $"This is a test email from the Meimad Planner Server on {Environment.MachineName}, sent at {sentAt:yyyy-MM-dd HH:mm} UTC.\r\n"
                + "The report email settings work.",
            IsBodyHtml = false
        };
        foreach (var recipient in recipients) message.To.Add(recipient);
        using var client = smtp.CreateClient(settings);
        client.Timeout = 30_000;
        await client.SendMailAsync(message, token);
        return new ReportEmailTestResult(recipients, sentAt, settings.SignsIn);
    }
}
