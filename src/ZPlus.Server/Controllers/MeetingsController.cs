using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ZPlus.Server.Data;
using ZPlus.Server.Models;
using ZPlus.Server.Services;
using ZPlus.Shared.Dtos;

namespace ZPlus.Server.Controllers;

[ApiController]
[Route("api/meetings")]
[Authorize]
public class MeetingsController(
    AppDbContext db,
    SettingsService settings,
    PasswordService passwords,
    EmailService email,
    SecretProtector protector) : ControllerBase
{
    private const int MaxInvites = 50;
    private const int MaxOccurrences = 52;
    private static readonly string[] Patterns = ["None", "Daily", "Weekly", "Monthly"];

    private Guid CurrentUserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static DateTime StepOccurrence(DateTime start, string pattern, int i) => pattern switch
    {
        "Daily" => start.AddDays(i),
        "Weekly" => start.AddDays(7 * i),
        "Monthly" => start.AddMonths(i),
        _ => start,
    };

    [HttpPost]
    public async Task<ActionResult<CreateMeetingResponse>> Create(CreateMeetingRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Topic))
            return BadRequest("A meeting topic is required.");

        if (string.IsNullOrEmpty(request.Password) && (await settings.GetAsync()).RequireMeetingPasswords)
            return BadRequest("Server policy requires a password on every meeting.");

        var inviteEmails = (request.InviteEmails ?? [])
            .Select(e => e.Trim().ToLowerInvariant())
            .Where(e => e.Length > 0)
            .Distinct()
            .ToList();
        if (inviteEmails.Count > MaxInvites)
            return BadRequest($"At most {MaxInvites} invitations per meeting.");
        var invalid = inviteEmails.Where(e => !e.Contains('@') || e.Contains(' ')).ToList();
        if (invalid.Count > 0)
            return BadRequest($"Invalid email address: {invalid[0]}");
        if (inviteEmails.Count > 0 && !await email.IsConfiguredAsync())
            return BadRequest("Email invitations are not available: the administrator has not configured an SMTP server.");

        // Recurrence.
        var pattern = (request.RecurrencePattern ?? "None").Trim();
        if (!Patterns.Contains(pattern))
            return BadRequest($"Recurrence must be one of: {string.Join(", ", Patterns)}.");
        int occurrences = 1;
        if (pattern != "None")
        {
            if (request.ScheduledStartUtc is null)
                return BadRequest("A recurring meeting needs a scheduled start date and time.");
            occurrences = Math.Clamp(request.RecurrenceCount, 1, MaxOccurrences);
        }
        int lead = Math.Max(0, request.ReminderLeadMinutes);
        var seriesId = pattern == "None" ? (Guid?)null : Guid.NewGuid();

        var host = await db.Users.FindAsync(CurrentUserId);
        if (host is null || host.IsDisabled) return Unauthorized();

        // The plaintext meeting password, encrypted so a deferred reminder can build a join link.
        var protectedPw = string.IsNullOrEmpty(request.Password) ? null : protector.Protect(request.Password);
        var now = DateTime.UtcNow;

        Meeting? first = null;
        int sent = 0, queued = 0;
        var failures = new List<string>();

        for (int i = 0; i < occurrences; i++)
        {
            var occStart = request.ScheduledStartUtc is null
                ? (DateTime?)null
                : StepOccurrence(request.ScheduledStartUtc.Value, pattern, i);

            var meeting = new Meeting
            {
                Topic = request.Topic.Trim(),
                HostUserId = host.Id,
                ScheduledStartUtc = occStart,
                DurationMinutes = request.DurationMinutes,
                WaitingRoomEnabled = request.WaitingRoomEnabled,
                SeriesId = seriesId,
                // Instant meetings (no scheduled start) are live immediately.
                IsActive = occStart is null,
            };
            if (!string.IsNullOrEmpty(request.Password))
                meeting.PasswordHash = passwords.Protect(request.Password);

            // Retry on the (astronomically unlikely) chance of a code collision.
            for (int attempt = 0; ; attempt++)
            {
                meeting.MeetingCode = MeetingCodeGenerator.NewCode();
                if (!await db.Meetings.AnyAsync(m => m.MeetingCode == meeting.MeetingCode)) break;
                if (attempt == 5) return StatusCode(500, "Could not allocate a meeting code.");
            }

            db.Meetings.Add(meeting);
            await db.SaveChangesAsync();
            first ??= meeting;

            // When to email invitations for this occurrence: a reminder lead time before the
            // start, or immediately when there is no lead / no scheduled start.
            var sendAt = (lead <= 0 || occStart is null) ? now : occStart.Value.AddMinutes(-lead);

            foreach (var address in inviteEmails)
            {
                if (sendAt <= now)
                {
                    // Due now — send inline (best effort) and record the attempt.
                    var error = await email.SendInviteAsync(meeting, address, request.Password, host.DisplayName);
                    db.MeetingInvitations.Add(new MeetingInvitation
                    {
                        MeetingId = meeting.Id, Email = address, InvitedByUserId = host.Id,
                        Sent = error is null, Error = error,
                    });
                    if (error is null) sent++;
                    else failures.Add($"{address}: {error}");
                }
                else
                {
                    // Queue a reminder for the background dispatcher to send at SendAtUtc.
                    db.MeetingInvitations.Add(new MeetingInvitation
                    {
                        MeetingId = meeting.Id, Email = address, InvitedByUserId = host.Id,
                        Sent = false, SendAtUtc = sendAt, ProtectedPassword = protectedPw,
                        IsReminder = true, HostDisplayName = host.DisplayName,
                    });
                    queued++;
                }
            }
        }
        if (inviteEmails.Count > 0) await db.SaveChangesAsync();

        return Ok(new CreateMeetingResponse(ToDto(first!, host.DisplayName), sent, failures, occurrences, queued));
    }

    /// <summary>Looks up a meeting by its code and validates the password, without joining it.</summary>
    [HttpPost("lookup")]
    public async Task<ActionResult<MeetingDto>> Lookup(JoinLookupRequest request)
    {
        var code = NormalizeCode(request.MeetingCode);
        var meeting = await db.Meetings.Include(m => m.Host)
            .SingleOrDefaultAsync(m => m.MeetingCode == code);
        if (meeting is null) return NotFound("No meeting found with that ID.");
        if (meeting.EndedAtUtc is not null) return Conflict("That meeting has already ended.");

        if (meeting.PasswordHash is not null)
        {
            if (string.IsNullOrEmpty(request.Password))
                return Unauthorized("This meeting requires a password.");
            if (!passwords.Verify(meeting.PasswordHash, request.Password))
                return Unauthorized("Incorrect meeting password.");
        }

        return Ok(ToDto(meeting, meeting.Host?.DisplayName ?? ""));
    }

    [HttpGet("mine")]
    public async Task<ActionResult<List<MeetingDto>>> Mine()
    {
        var userId = CurrentUserId;
        var meetings = await db.Meetings.Include(m => m.Host)
            .Where(m => m.HostUserId == userId && m.EndedAtUtc == null)
            .OrderByDescending(m => m.CreatedAtUtc)
            .Take(50)
            .ToListAsync();
        return Ok(meetings.Select(m => ToDto(m, m.Host?.DisplayName ?? "")).ToList());
    }

    private static string NormalizeCode(string raw)
    {
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        return digits.Length == 9 ? $"{digits[..3]}-{digits[3..6]}-{digits[6..]}" : raw.Trim();
    }

    internal static MeetingDto ToDto(Meeting m, string hostDisplayName) => new(
        m.Id, m.MeetingCode, m.Topic, m.HostUserId, hostDisplayName,
        m.ScheduledStartUtc, m.DurationMinutes, m.PasswordHash is not null,
        m.IsActive, m.CreatedAtUtc);
}
