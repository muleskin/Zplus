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
    EmailService email) : ControllerBase
{
    private const int MaxInvites = 50;

    private Guid CurrentUserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

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

        var host = await db.Users.FindAsync(CurrentUserId);
        if (host is null || host.IsDisabled) return Unauthorized();

        var meeting = new Meeting
        {
            Topic = request.Topic.Trim(),
            HostUserId = host.Id,
            ScheduledStartUtc = request.ScheduledStartUtc,
            DurationMinutes = request.DurationMinutes,
            // Instant meetings (no scheduled start) are live immediately.
            IsActive = request.ScheduledStartUtc is null,
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

        // Send invitations (best effort per address) and record each attempt.
        int sent = 0;
        var failures = new List<string>();
        foreach (var address in inviteEmails)
        {
            string? error = await email.SendInviteAsync(meeting, address, request.Password, host.DisplayName);
            db.MeetingInvitations.Add(new MeetingInvitation
            {
                MeetingId = meeting.Id,
                Email = address,
                InvitedByUserId = host.Id,
                Sent = error is null,
                Error = error,
            });
            if (error is null) sent++;
            else failures.Add($"{address}: {error}");
        }
        if (inviteEmails.Count > 0) await db.SaveChangesAsync();

        return Ok(new CreateMeetingResponse(ToDto(meeting, host.DisplayName), sent, failures));
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
