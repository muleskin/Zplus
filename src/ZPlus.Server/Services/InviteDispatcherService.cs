using Microsoft.EntityFrameworkCore;
using ZPlus.Server.Data;

namespace ZPlus.Server.Services;

/// <summary>
/// Sends queued meeting reminder invitations when they come due. Invitations with a
/// <c>SendAtUtc</c> in the past and <c>Sent = false</c> are dispatched here, so reminders for
/// scheduled and recurring meetings go out a chosen lead time before each occurrence.
/// Poll interval defaults to 60s (override with ZPLUS_INVITE_POLL_SECONDS, min 15).
/// </summary>
public class InviteDispatcherService(
    IServiceScopeFactory scopeFactory,
    ILogger<InviteDispatcherService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(
        int.TryParse(Environment.GetEnvironmentVariable("ZPLUS_INVITE_POLL_SECONDS"), out var s) ? Math.Max(15, s) : 60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await DispatchDueAsync(stoppingToken); }
            catch (Exception ex) { logger.LogWarning("Invite dispatch pass failed: {Error}", ex.Message); }
            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task DispatchDueAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var email = scope.ServiceProvider.GetRequiredService<EmailService>();
        var protector = scope.ServiceProvider.GetRequiredService<SecretProtector>();

        var now = DateTime.UtcNow;
        var due = await db.MeetingInvitations
            .Where(i => !i.Sent && i.SendAtUtc != null && i.SendAtUtc <= now)
            .OrderBy(i => i.SendAtUtc)
            .Take(100)
            .ToListAsync(ct);
        if (due.Count == 0) return;

        foreach (var inv in due)
        {
            var meeting = await db.Meetings.FirstOrDefaultAsync(m => m.Id == inv.MeetingId, ct);
            if (meeting is null || meeting.EndedAtUtc is not null)
            {
                // Meeting gone or already ended — mark handled so it isn't retried forever.
                inv.Sent = true;
                inv.Error = "Meeting no longer available.";
                inv.SendAtUtc = null;
                inv.ProtectedPassword = null;
                continue;
            }

            var password = string.IsNullOrEmpty(inv.ProtectedPassword) ? null : protector.Unprotect(inv.ProtectedPassword);
            var error = await email.SendInviteAsync(meeting, inv.Email, password,
                inv.HostDisplayName ?? "Your host", isReminder: inv.IsReminder);

            inv.Error = error;
            if (error is null)
            {
                inv.Sent = true;
                inv.SendAtUtc = null;
                inv.ProtectedPassword = null; // drop the recoverable secret once delivered
            }
            else
            {
                // Back off and retry later rather than hammering a misconfigured mail server.
                inv.SendAtUtc = now.AddMinutes(15);
                logger.LogWarning("Reminder to {Email} for {Code} failed: {Error}", inv.Email, meeting.MeetingCode, error);
            }
        }
        await db.SaveChangesAsync(ct);
    }
}
