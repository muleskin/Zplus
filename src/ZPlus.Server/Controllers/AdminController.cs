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
    AuditService audit,
    TotpService totp,
    SecretProtector protector,
    IHubContext<MeetingHub> hub) : ControllerBase
{
    private Guid CurrentUserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private string CurrentEmail => User.FindFirstValue(ClaimTypes.Email) ?? "";
    private bool IsSuperAdmin => User.IsInRole(Roles.SuperAdmin);

    // ---- Users -------------------------------------------------------------

    [HttpGet("users")]
    public async Task<ActionResult<List<AdminUserDto>>> GetUsers()
    {
        var users = await db.Users.AsNoTracking()
            .OrderBy(u => u.Email)
            .Select(u => new AdminUserDto(u.Id, u.Email, u.DisplayName, u.Role, u.IsDisabled, u.CreatedAtUtc, u.MfaEnabled, u.MfaRequired))
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
        await audit.LogAsync(CurrentUserId, CurrentEmail, "user.create", $"{user.Email} ({user.Role})");
        return Ok(new AdminUserDto(user.Id, user.Email, user.DisplayName, user.Role, user.IsDisabled, user.CreatedAtUtc, user.MfaEnabled, user.MfaRequired));
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
        await audit.LogAsync(CurrentUserId, CurrentEmail, "user.update",
            $"{user.Email}: role={user.Role}, disabled={user.IsDisabled}");
        return Ok(new AdminUserDto(user.Id, user.Email, user.DisplayName, user.Role, user.IsDisabled, user.CreatedAtUtc, user.MfaEnabled, user.MfaRequired));
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
        await audit.LogAsync(CurrentUserId, CurrentEmail, "user.reset-password", user.Email);
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

        // For an HTTPS listen URL, verify the certificate loads before saving so the
        // server won't fail to start after a restart. The path is on the server machine.
        if (uri.Scheme == "https")
        {
            if (string.IsNullOrWhiteSpace(request.CertPath))
                return BadRequest("An https:// listen URL requires a certificate path (a .pfx file on the server).");
            if (!System.IO.File.Exists(request.CertPath.Trim()))
                return BadRequest($"Certificate file not found on the server: {request.CertPath.Trim()}");
            var keyPath = request.CertKeyPath.Trim();
            if (!string.IsNullOrWhiteSpace(keyPath) && !System.IO.File.Exists(keyPath))
                return BadRequest($"Private-key file not found on the server: {keyPath}");
            var certPw = string.IsNullOrEmpty(request.CertPassword)
                ? await settings.GetCertPasswordAsync()
                : request.CertPassword;
            try
            {
                using var _ = ZPlus.Server.CertificateLoader.Load(request.CertPath.Trim(), keyPath, certPw);
            }
            catch (Exception ex)
            {
                return BadRequest($"Could not load the certificate (check the files and password): {ex.Message}");
            }
        }

        await settings.SaveAsync(request);
        await audit.LogAsync(CurrentUserId, CurrentEmail, "settings.save", $"listen={request.ListenUrl}, provider={request.EmailProvider}");
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
        await audit.LogAsync(CurrentUserId, CurrentEmail, "meeting.force-end", $"{meeting.MeetingCode} ({meeting.Topic})");
        return Ok();
    }

    // ---- Dashboard ---------------------------------------------------------------

    [HttpGet("stats")]
    public async Task<ActionResult<DashboardStatsDto>> GetStats()
    {
        var todayUtc = DateTime.UtcNow.Date;
        int totalUsers = await db.Users.CountAsync();
        int admins = await db.Users.CountAsync(u => u.Role == Roles.Admin || u.Role == Roles.SuperAdmin);
        int disabled = await db.Users.CountAsync(u => u.IsDisabled);
        int mfa = await db.Users.CountAsync(u => u.MfaEnabled);
        int meetingsTotal = await db.Meetings.CountAsync();
        int meetingsToday = await db.Meetings.CountAsync(m => m.CreatedAtUtc >= todayUtc);
        int messages = await db.ChatMessages.CountAsync();

        var activeMeetings = await db.Meetings
            .Where(m => m.IsActive && m.EndedAtUtc == null)
            .Select(m => m.Id).ToListAsync();
        int activeParticipants = activeMeetings.Sum(id => state.Get(id)?.Participants.Count ?? 0);

        return Ok(new DashboardStatsDto(
            totalUsers, admins, disabled, mfa,
            activeMeetings.Count, activeParticipants,
            meetingsToday, meetingsTotal, messages));
    }

    // ---- Audit log ---------------------------------------------------------------

    [HttpGet("audit")]
    public async Task<ActionResult<List<AuditLogDto>>> GetAudit()
    {
        var logs = await db.AuditLogs.AsNoTracking()
            .OrderByDescending(a => a.Id)
            .Take(300)
            .Select(a => new AuditLogDto(a.Id, a.WhenUtc, a.ActorEmail, a.Action, a.Details))
            .ToListAsync();
        return Ok(logs);
    }

    // ---- Smart search ------------------------------------------------------------

    [HttpGet("search")]
    public async Task<ActionResult<SearchResultsDto>> Search([FromQuery] string q)
    {
        q = (q ?? "").Trim();
        if (q.Length == 0) return Ok(new SearchResultsDto([], []));
        var like = q.ToLowerInvariant();

        var users = await db.Users.AsNoTracking()
            .Where(u => u.Email.ToLower().Contains(like) || u.DisplayName.ToLower().Contains(like))
            .OrderBy(u => u.Email).Take(50)
            .Select(u => new AdminUserDto(u.Id, u.Email, u.DisplayName, u.Role, u.IsDisabled, u.CreatedAtUtc, u.MfaEnabled, u.MfaRequired))
            .ToListAsync();

        var meetings = await db.Meetings.AsNoTracking().Include(m => m.Host)
            .Where(m => m.Topic.ToLower().Contains(like) || m.MeetingCode.Contains(q))
            .OrderByDescending(m => m.CreatedAtUtc).Take(50)
            .Select(m => new AdminMeetingDto(m.Id, m.MeetingCode, m.Topic, m.Host!.DisplayName, m.IsActive, m.CreatedAtUtc, m.EndedAtUtc))
            .ToListAsync();

        return Ok(new SearchResultsDto(users, meetings));
    }

    // ---- MFA management ----------------------------------------------------------

    /// <summary>Requires MFA for a user: they must enroll an authenticator on next sign-in.</summary>
    [HttpPost("users/{id:guid}/mfa/require")]
    public async Task<ActionResult<AdminUserDto>> RequireMfa(Guid id)
    {
        var user = await db.Users.FindAsync(id);
        if (user is null) return NotFound("User not found.");
        if (user.Role == Roles.SuperAdmin && !IsSuperAdmin)
            return StatusCode(403, "Only a super admin can change a super admin's MFA.");

        user.MfaRequired = true;
        user.MfaEnabled = false;
        user.TotpSecret = null;
        await db.SaveChangesAsync();
        await audit.LogAsync(CurrentUserId, CurrentEmail, "user.mfa-require", user.Email);
        return Ok(new AdminUserDto(user.Id, user.Email, user.DisplayName, user.Role, user.IsDisabled, user.CreatedAtUtc, user.MfaEnabled, user.MfaRequired));
    }

    /// <summary>Disables MFA for a user and clears their enrollment.</summary>
    [HttpPost("users/{id:guid}/mfa/reset")]
    public async Task<ActionResult<AdminUserDto>> ResetMfa(Guid id)
    {
        var user = await db.Users.FindAsync(id);
        if (user is null) return NotFound("User not found.");
        if (user.Role == Roles.SuperAdmin && !IsSuperAdmin)
            return StatusCode(403, "Only a super admin can change a super admin's MFA.");

        user.MfaRequired = false;
        user.MfaEnabled = false;
        user.TotpSecret = null;
        await db.SaveChangesAsync();
        await audit.LogAsync(CurrentUserId, CurrentEmail, "user.mfa-reset", user.Email);
        return Ok(new AdminUserDto(user.Id, user.Email, user.DisplayName, user.Role, user.IsDisabled, user.CreatedAtUtc, user.MfaEnabled, user.MfaRequired));
    }
}
