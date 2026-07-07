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

        try
        {
            using var smtp = new SmtpClient(config.SmtpHost, config.SmtpPort)
            {
                // STARTTLS on the standard submission port; implicit-TLS (465) is not
                // supported by SmtpClient — use 587 with providers that offer both.
                EnableSsl = config.SmtpPort == 587,
                DeliveryMethod = SmtpDeliveryMethod.Network,
            };
            var smtpPassword = await settings.GetSmtpPasswordAsync();
            if (!string.IsNullOrEmpty(config.SmtpUser))
                smtp.Credentials = new NetworkCredential(config.SmtpUser, smtpPassword);

            using var message = new MailMessage(from, email)
            {
                Subject = $"Z+ meeting invitation: {meeting.Topic}",
                Body = body.ToString(),
            };
            await smtp.SendMailAsync(message);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Invite to {Email} for {Code} failed: {Error}", email, meeting.MeetingCode, ex.Message);
            return ex.Message;
        }
    }
}
