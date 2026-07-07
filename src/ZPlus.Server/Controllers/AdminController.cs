using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using ZPlus.Server.Data;
using ZPlus.Server.Hubs;
using ZPlus.Server.Models;
using ZPlus.Server.Services;
using ZPlus.Shared.Dtos;

namespace ZPlus.Server.Controllers;

[ApiController]
[Route("api/admin")]
[Authorize(Roles = $"{Roles.Admin},{Roles.SuperAdmin}")]
public class AdminController(
    AppDbContext db,
    SettingsService settings,
    MeetingStateStore state,
    PasswordService passwords,
    EmailService email,
    IHubContext<MeetingHub> hub) : ControllerBase
{
    private Guid CurrentUserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private bool IsSuperAdmin => User.IsInRole(Roles.SuperAdmin);

    // ---- Users -------------------------------------------------------------

    [HttpGet("users")]
    public async Task<ActionResult<List<AdminUserDto>>> GetUsers()
    {
        var users = await db.Users.AsNoTracking()
            .OrderBy(u => u.Email)
            .Select(u => new AdminUserDto(u.Id, u.Email, u.DisplayName, u.Role, u.IsDisabled, u.CreatedAtUtc))
            .ToListAsync();
        return Ok(users);
    }

    [HttpPost("users")]
    public async Task<ActionResult<AdminUserDto>> CreateUser(AdminCreateUserRequest request)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
            return BadRequest("A valid email address is required.");
        if (string.IsNullOrWhiteSpace(request.DisplayName))
            return BadRequest("A display name is required.");
        if (request.Password.Length < 8)
            return BadRequest("Password must be at least 8 characters.");
        if (!Roles.All.Contains(request.Role))
            return BadRequest($"Role must be one of: {string.Join(", ", Roles.All)}.");
        if (request.Role == Roles.SuperAdmin && !IsSuperAdmin)
            return StatusCode(403, "Only a super admin can create super admin accounts.");
        if (await db.Users.AnyAsync(u => u.Email == email))
            return Conflict("An account with that email already exists.");

        var user = new User { Email = email, DisplayName = request.DisplayName.Trim(), Role = request.Role };
        user.PasswordHash = passwords.Protect(request.Password);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return Ok(new AdminUserDto(user.Id, user.Email, user.DisplayName, user.Role, user.IsDisabled, user.CreatedAtUtc));
    }

    [HttpPut("users/{id:guid}")]
    public async Task<ActionResult<AdminUserDto>> UpdateUser(Guid id, AdminUpdateUserRequest request)
    {
        var user = await db.Users.FindAsync(id);
        if (user is null) return NotFound("User not found.");

        if (user.Role == Roles.SuperAdmin && !IsSuperAdmin)
            return StatusCode(403, "Only a super admin can modify super admin accounts.");

        if (request.Role is not null && request.Role != user.Role)
        {
            if (!Roles.All.Contains(request.Role))
                return BadRequest($"Role must be one of: {string.Join(", ", Roles.All)}.");
            if (request.Role == Roles.SuperAdmin && !IsSuperAdmin)
                return StatusCode(403, "Only a super admin can grant the super admin role.");
            if (id == CurrentUserId)
                return BadRequest("You cannot change your own role.");
            user.Role = request.Role;
        }

        if (request.IsDisabled is not null && request.IsDisabled != user.IsDisabled)
        {
            if (id == CurrentUserId)
                return BadRequest("You cannot disable your own account.");
            user.IsDisabled = request.IsDisabled.Value;
        }

        if (!string.IsNullOrWhiteSpace(request.DisplayName))
            user.DisplayName = request.DisplayName.Trim();

        await db.SaveChangesAsync();
        return Ok(new AdminUserDto(user.Id, user.Email, user.DisplayName, user.Role, user.IsDisabled, user.CreatedAtUtc));
    }

    [HttpPost("users/{id:guid}/reset-password")]
    public async Task<IActionResult> ResetPassword(Guid id, AdminResetPasswordRequest request)
    {
        if (request.NewPassword.Length < 8)
            return BadRequest("Password must be at least 8 characters.");

        var user = await db.Users.FindAsync(id);
        if (user is null) return NotFound("User not found.");
        if (user.Role == Roles.SuperAdmin && !IsSuperAdmin)
            return StatusCode(403, "Only a super admin can reset a super admin's password.");

        user.PasswordHash = passwords.Protect(request.NewPassword);
        await db.SaveChangesAsync();
        return Ok();
    }

    // ---- Server settings ------------------------------------------------------

    [HttpGet("settings")]
    public async Task<ActionResult<ServerSettingsDto>> GetSettings() => Ok(await settings.GetAsync());

    [HttpPut("settings")]
    public async Task<ActionResult<ServerSettingsDto>> SaveSettings(ServerSettingsDto request)
    {
        if (request.MaxParticipantsPerMeeting is < 2 or > 1000)
            return BadRequest("Max participants must be between 2 and 1000.");
        if (!Uri.TryCreate(request.ListenUrl.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
            return BadRequest("Listen URL must be an absolute http:// or https:// address.");
        if (request.SmtpPort is < 1 or > 65535)
            return BadRequest("SMTP port must be between 1 and 65535.");
        if (!string.IsNullOrWhiteSpace(request.PublicUrl) &&
            (!Uri.TryCreate(request.PublicUrl.Trim(), UriKind.Absolute, out var publicUri) ||
             (publicUri.Scheme != "http" && publicUri.Scheme != "https")))
            return BadRequest("Public URL must be empty or an absolute http:// or https:// address.");
        await settings.SaveAsync(request);
        return Ok(await settings.GetAsync());
    }

    /// <summary>Sends a test email with the supplied (possibly unsaved) settings so the admin can verify SMTP.</summary>
    [HttpPost("settings/test-email")]
    public async Task<IActionResult> TestEmail(SmtpTestRequest request)
    {
        if (request.Settings.SmtpPort is < 1 or > 65535)
            return BadRequest("SMTP port must be between 1 and 65535.");
        var error = await email.SendTestAsync(request.Settings, request.Recipient);
        return error is null ? Ok() : BadRequest(error);
    }

    // ---- Meetings ----------------------------------------------------------------

    [HttpGet("meetings/active")]
    public async Task<ActionResult<List<ActiveMeetingDto>>> GetActiveMeetings()
    {
        var meetings = await db.Meetings.AsNoTracking()
            .Include(m => m.Host)
            .Where(m => m.IsActive && m.EndedAtUtc == null)
            .OrderByDescending(m => m.CreatedAtUtc)
            .ToListAsync();

        var result = meetings.Select(m => new ActiveMeetingDto(
            m.Id, m.MeetingCode, m.Topic, m.Host?.DisplayName ?? "",
            state.Get(m.Id)?.Participants.Count ?? 0,
            m.CreatedAtUtc)).ToList();
        return Ok(result);
    }

    /// <summary>Force-ends a meeting: notifies every connected participant and closes the record.</summary>
    [HttpPost("meetings/{id:guid}/end")]
    public async Task<IActionResult> ForceEndMeeting(Guid id)
    {
        var meeting = await db.Meetings.FindAsync(id);
        if (meeting is null) return NotFound("Meeting not found.");
        if (!meeting.IsActive && meeting.EndedAtUtc is not null) return Conflict("Meeting already ended.");

        meeting.IsActive = false;
        meeting.EndedAtUtc = DateTime.UtcNow;
        var openRecords = await db.MeetingParticipants
            .Where(p => p.MeetingId == id && p.LeftAtUtc == null)
            .ToListAsync();
        foreach (var r in openRecords) r.LeftAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();

        await hub.Clients.Group(MeetingHub.GroupName(id)).SendAsync(HubEvents.MeetingEnded);
        state.EndMeeting(id);
        return Ok();
    }
}
