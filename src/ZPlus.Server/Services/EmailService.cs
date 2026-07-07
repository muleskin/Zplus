using System.Net;
using System.Net.Mail;
using ZPlus.Server.Models;
using ZPlus.Shared.Dtos;

namespace ZPlus.Server.Services;

/// <summary>
/// Sends meeting invitation emails via the SMTP server configured in server settings.
/// Email is disabled until an admin sets an SMTP host.
/// </summary>
public class EmailService(SettingsService settings, ILogger<EmailService> logger)
{
    public async Task<bool> IsConfiguredAsync() =>
        !string.IsNullOrWhiteSpace((await settings.GetAsync()).SmtpHost);

    /// <summary>Sends one invitation. Returns null on success, or a short error description.</summary>
    public async Task<string?> SendInviteAsync(Meeting meeting, string email, string? meetingPassword, string hostDisplayName)
    {
        var config = await settings.GetAsync();
        if (string.IsNullOrWhiteSpace(config.SmtpHost))
            return "Email is not configured on this server (set an SMTP host in server settings).";

        var from = string.IsNullOrWhiteSpace(config.SmtpFrom)
            ? $"zplus@{config.SmtpHost}"
            : config.SmtpFrom;

        string when = meeting.ScheduledStartUtc is null
            ? "The meeting is running now."
            : $"When: {meeting.ScheduledStartUtc.Value:dddd, MMMM d yyyy HH:mm} UTC" +
              (meeting.DurationMinutes is int minutes ? $" ({minutes} minutes)" : "");

        var body = new System.Text.StringBuilder();
        body.AppendLine($"{hostDisplayName} has invited you to a Z+ meeting.");
        body.AppendLine();
        body.AppendLine($"Topic:      {meeting.Topic}");
        body.AppendLine($"{when}");
        body.AppendLine($"Meeting ID: {meeting.MeetingCode}");
        if (!string.IsNullOrEmpty(meetingPassword))
            body.AppendLine($"Password:   {meetingPassword}");
        body.AppendLine();
        if (!string.IsNullOrWhiteSpace(config.PublicUrl))
        {
            body.AppendLine($"Invitation link: {config.PublicUrl}/join/{meeting.MeetingCode}");
            body.AppendLine();
            body.AppendLine($"To join: open Z+, set the server to {config.PublicUrl}, and enter the meeting ID above.");
        }
        else
        {
            body.AppendLine("To join: open Z+ and enter the meeting ID above.");
        }

        var smtpPassword = await settings.GetSmtpPasswordAsync();
        var error = await SendAsync(config, smtpPassword, from, email,
            $"Z+ meeting invitation: {meeting.Topic}", body.ToString());
        if (error is not null)
            logger.LogWarning("Invite to {Email} for {Code} failed: {Error}", email, meeting.MeetingCode, error);
        return error;
    }

    /// <summary>
    /// Sends a test email using the supplied settings (which may be unsaved form values).
    /// A blank password in the settings resolves to the stored one. Returns null on
    /// success, or the SMTP error so the admin can diagnose configuration problems.
    /// </summary>
    public async Task<string?> SendTestAsync(ServerSettingsDto config, string recipient)
    {
        if (string.IsNullOrWhiteSpace(config.SmtpHost))
            return "Enter an SMTP host first.";
        if (string.IsNullOrWhiteSpace(recipient) || !recipient.Contains('@'))
            return "Enter a valid test recipient email address.";

        // Blank password means "use the one already stored on the server".
        string password = string.IsNullOrEmpty(config.SmtpPassword)
            ? await settings.GetSmtpPasswordAsync()
            : config.SmtpPassword;

        var from = string.IsNullOrWhiteSpace(config.SmtpFrom) ? $"zplus@{config.SmtpHost}" : config.SmtpFrom;
        var body =
            "This is a test message from your Z+ server confirming that email delivery is working.\r\n\r\n" +
            "If you received this, meeting invitations will be delivered with these settings.";

        var error = await SendAsync(config, password, from, recipient.Trim(), "Z+ email test", body);
        if (error is not null)
            logger.LogWarning("Test email to {To} failed: {Error}", recipient, error);
        return error;
    }

    /// <summary>Core SMTP send shared by invites and tests. Returns null on success, else the error.</summary>
    private static async Task<string?> SendAsync(
        ServerSettingsDto config, string password, string from, string to, string subject, string body)
    {
        try
        {
            using var smtp = new SmtpClient(config.SmtpHost, config.SmtpPort)
            {
                // STARTTLS on the standard submission port; implicit-TLS (465) is not
                // supported by SmtpClient — use 587 with providers that offer both.
                EnableSsl = config.SmtpPort == 587,
                DeliveryMethod = SmtpDeliveryMethod.Network,
            };
            if (!string.IsNullOrEmpty(config.SmtpUser))
                smtp.Credentials = new NetworkCredential(config.SmtpUser, password);

            using var message = new MailMessage(from, to) { Subject = subject, Body = body };
            await smtp.SendMailAsync(message);
            return null;
        }
        catch (Exception ex)
        {
            // The useful detail for connection failures (refused/timeout/DNS) is in the
            // inner exception; append it so the admin can actually diagnose the problem.
            return ex.InnerException is { Message: var inner } && inner != ex.Message
                ? $"{ex.Message} ({inner})"
                : ex.Message;
        }
    }
}
