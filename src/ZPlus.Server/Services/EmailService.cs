using System.Net;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Text;
using ZPlus.Server.Models;
using ZPlus.Shared.Dtos;

namespace ZPlus.Server.Services;

/// <summary>
/// Sends meeting invitation and test emails. Two transports are supported, chosen by the
/// EmailProvider setting: classic SMTP, or the Mailgun HTTP API authenticated with a domain
/// sending key. Email is disabled until the active provider is configured.
/// </summary>
public class EmailService(SettingsService settings, ILogger<EmailService> logger)
{
    private static readonly HttpClient MailgunHttp = new();

    private static bool IsMailgun(ServerSettingsDto c) =>
        c.EmailProvider.Equals("Mailgun", StringComparison.OrdinalIgnoreCase);

    public async Task<bool> IsConfiguredAsync()
    {
        var c = await settings.GetAsync();
        return IsMailgun(c)
            ? !string.IsNullOrWhiteSpace(c.MailgunDomain)
            : !string.IsNullOrWhiteSpace(c.SmtpHost);
    }

    /// <summary>Sends one invitation. Returns null on success, or a short error description.</summary>
    public async Task<string?> SendInviteAsync(Meeting meeting, string email, string? meetingPassword, string hostDisplayName,
        bool isReminder = false, DateTime? occurrenceStartUtc = null)
    {
        var config = await settings.GetAsync();
        if (!await IsConfiguredAsync())
            return "Email is not configured on this server (set it up in server settings).";

        var from = FromAddress(config);

        // For a recurring reminder, show this occurrence's date rather than the series' first date.
        var startUtc = occurrenceStartUtc ?? meeting.ScheduledStartUtc;
        string when = startUtc is null
            ? "The meeting is running now."
            : $"When: {startUtc.Value:dddd, MMMM d yyyy HH:mm} UTC" +
              (meeting.DurationMinutes is int minutes ? $" ({minutes} minutes)" : "");

        var body = new StringBuilder();
        body.AppendLine(isReminder
            ? $"Reminder: {hostDisplayName} invited you to a Z+ meeting starting soon."
            : $"{hostDisplayName} has invited you to a Z+ meeting.");
        body.AppendLine();
        body.AppendLine($"Topic:      {meeting.Topic}");
        body.AppendLine($"{when}");
        if (meeting.IsRecurring)
            body.AppendLine($"Repeats:    {meeting.RecurrencePattern}, {meeting.RecurrenceCount} occurrences (same meeting ID each time)");
        body.AppendLine($"Meeting ID: {meeting.MeetingCode}");
        if (!string.IsNullOrEmpty(meetingPassword))
            body.AppendLine($"Password:   {meetingPassword}");
        body.AppendLine();
        if (!string.IsNullOrWhiteSpace(config.PublicUrl))
        {
            var pw = string.IsNullOrEmpty(meetingPassword) ? "" : $"?pw={Uri.EscapeDataString(meetingPassword)}";
            body.AppendLine($"Join the meeting: {config.PublicUrl}/join/{meeting.MeetingCode}{pw}");
            body.AppendLine("(opens a page with a button to launch the Z+ app)");
            body.AppendLine();
            // Direct one-click link for mail clients that recognise custom URI schemes.
            body.AppendLine("One-click join (if your app is installed):");
            body.AppendLine(ZPlus.Shared.ZplusLink.BuildJoin(config.PublicUrl, meeting.MeetingCode, meetingPassword));
        }
        else
        {
            body.AppendLine("To join: open Z+ and enter the meeting ID above.");
        }

        var secret = await ActiveSecretAsync(config);
        var subject = (isReminder ? "Reminder — Z+ meeting: " : "Z+ meeting invitation: ") + meeting.Topic;
        var error = await SendAsync(config, secret, from, email, subject, body.ToString());
        if (error is not null)
            logger.LogWarning("Invite to {Email} for {Code} failed: {Error}", email, meeting.MeetingCode, error);
        return error;
    }

    /// <summary>
    /// Sends a test email using the supplied settings (which may be unsaved form values).
    /// A blank secret in the settings resolves to the stored one. Returns null on success,
    /// or the provider error so the admin can diagnose configuration problems.
    /// </summary>
    public async Task<string?> SendTestAsync(ServerSettingsDto config, string recipient)
    {
        if (string.IsNullOrWhiteSpace(recipient) || !recipient.Contains('@'))
            return "Enter a valid test recipient email address.";
        if (IsMailgun(config))
        {
            if (string.IsNullOrWhiteSpace(config.MailgunDomain))
                return "Enter your Mailgun sending domain first.";
        }
        else if (string.IsNullOrWhiteSpace(config.SmtpHost))
        {
            return "Enter an SMTP host first.";
        }

        // A blank secret in the form means "use the one already stored on the server".
        string secret = IsMailgun(config)
            ? (string.IsNullOrEmpty(config.MailgunApiKey) ? await settings.GetMailgunApiKeyAsync() : config.MailgunApiKey)
            : (string.IsNullOrEmpty(config.SmtpPassword) ? await settings.GetSmtpPasswordAsync() : config.SmtpPassword);

        var body =
            "This is a test message from your Z+ server confirming that email delivery is working.\r\n\r\n" +
            "If you received this, meeting invitations will be delivered with these settings.";

        var error = await SendAsync(config, secret, FromAddress(config), recipient.Trim(), "Z+ email test", body);
        if (error is not null)
            logger.LogWarning("Test email to {To} failed: {Error}", recipient, error);
        return error;
    }

    // ---- transports ---------------------------------------------------------

    private async Task<string> ActiveSecretAsync(ServerSettingsDto config) =>
        IsMailgun(config) ? await settings.GetMailgunApiKeyAsync() : await settings.GetSmtpPasswordAsync();

    private static string FromAddress(ServerSettingsDto config) =>
        !string.IsNullOrWhiteSpace(config.SmtpFrom) ? config.SmtpFrom
        : IsMailgun(config) ? $"zplus@{config.MailgunDomain}"
        : $"zplus@{config.SmtpHost}";

    /// <summary>Dispatches to the configured transport. Returns null on success, else the error.</summary>
    private static async Task<string?> SendAsync(
        ServerSettingsDto config, string secret, string from, string to, string subject, string body)
    {
        try
        {
            return IsMailgun(config)
                ? await SendViaMailgunAsync(config, secret, from, to, subject, body)
                : await SendViaSmtpAsync(config, secret, from, to, subject, body);
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

    private static async Task<string?> SendViaSmtpAsync(
        ServerSettingsDto config, string password, string from, string to, string subject, string body)
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

    /// <summary>
    /// Sends through Mailgun's HTTP API using HTTP basic auth of "api:&lt;sending key&gt;".
    /// The sending key is a send-only, domain-scoped credential (Mailgun's recommended method).
    /// </summary>
    private static async Task<string?> SendViaMailgunAsync(
        ServerSettingsDto config, string apiKey, string from, string to, string subject, string body)
    {
        if (string.IsNullOrWhiteSpace(config.MailgunDomain))
            return "Enter your Mailgun sending domain.";
        if (string.IsNullOrEmpty(apiKey))
            return "Enter your Mailgun sending key.";

        var baseUrl = config.MailgunRegion.Equals("eu", StringComparison.OrdinalIgnoreCase)
            ? "https://api.eu.mailgun.net"
            : "https://api.mailgun.net";

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v3/{config.MailgunDomain}/messages");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($"api:{apiKey}")));
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["from"] = from,
            ["to"] = to,
            ["subject"] = subject,
            ["text"] = body,
        });

        using var response = await MailgunHttp.SendAsync(request);
        if (response.IsSuccessStatusCode) return null;

        // Mailgun returns a JSON body like {"message":"..."} describing the problem.
        var detail = (await response.Content.ReadAsStringAsync()).Trim();
        if (detail.Length > 300) detail = detail[..300];
        return $"Mailgun API returned {(int)response.StatusCode} {response.ReasonPhrase}" +
               (detail.Length > 0 ? $": {detail}" : ".");
    }
}
