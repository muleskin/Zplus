using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ZPlus.Server.Data;
using ZPlus.Server.Models;
using ZPlus.Server.Services;
using ZPlus.Shared.Dtos;

namespace ZPlus.Server.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController(
    AppDbContext db,
    TokenService tokenService,
    SettingsService settings,
    PasswordService passwords,
    TotpService totp,
    SecretProtector protector,
    AuditService audit) : ControllerBase
{

    [HttpPost("register")]
    public async Task<ActionResult<AuthResponse>> Register(RegisterRequest request)
    {
        if (!(await settings.GetAsync()).AllowSelfRegistration)
            return StatusCode(403, "Self-registration is disabled. Ask an administrator to create your account.");

        var email = request.Email.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
            return BadRequest("A valid email address is required.");
        if (string.IsNullOrWhiteSpace(request.DisplayName))
            return BadRequest("A display name is required.");
        if (request.Password.Length < 8)
            return BadRequest("Password must be at least 8 characters.");

        if (await db.Users.AnyAsync(u => u.Email == email))
            return Conflict("An account with that email already exists.");

        var user = new User { Email = email, DisplayName = request.DisplayName.Trim() };
        user.PasswordHash = passwords.Protect(request.Password);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        return Ok(new AuthResponse(tokenService.CreateToken(user), ToDto(user)));
    }

    [HttpPost("login")]
    public async Task<ActionResult<AuthResponse>> Login(LoginRequest request)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var user = await db.Users.SingleOrDefaultAsync(u => u.Email == email);
        if (user is null)
            return Unauthorized("Invalid email or password.");

        if (!passwords.Verify(user.PasswordHash, request.Password))
            return Unauthorized("Invalid email or password.");

        if (user.IsDisabled)
            return Unauthorized("This account has been disabled. Contact your administrator.");

        // Multi-factor authentication (TOTP).
        if (user.MfaRequired && !user.MfaEnabled)
        {
            // First leg: issue a fresh secret so the user can add Z+ to their authenticator.
            if (string.IsNullOrWhiteSpace(request.MfaCode) || string.IsNullOrWhiteSpace(request.MfaEnrollSecret))
            {
                var secret = totp.GenerateSecret();
                var uri = totp.BuildOtpauthUri(secret, "Z+", user.Email);
                return Ok(new AuthResponse(null, null, MfaRequired: true,
                    Enrollment: new MfaEnrollmentDto(secret, uri, "Z+", user.Email)));
            }
            // Second leg: confirm the first code, then persist the (encrypted) secret.
            if (!totp.Verify(request.MfaEnrollSecret, request.MfaCode))
                return Unauthorized("That code didn't match. Enter a fresh 6-digit code from your authenticator.");
            user.TotpSecret = protector.Protect(request.MfaEnrollSecret);
            user.MfaEnabled = true;
            user.MfaRequired = false;
            await db.SaveChangesAsync();
            await audit.LogAsync(user.Id, user.Email, "auth.mfa-enrolled");
        }
        else if (user.MfaEnabled)
        {
            if (string.IsNullOrWhiteSpace(request.MfaCode))
                return Ok(new AuthResponse(null, null, MfaRequired: true));
            var secret = user.TotpSecret is null ? null : protector.Unprotect(user.TotpSecret);
            if (secret is null || !totp.Verify(secret, request.MfaCode))
                return Unauthorized("Invalid authentication code.");
        }

        if (user.Role is Roles.Admin or Roles.SuperAdmin)
            await audit.LogAsync(user.Id, user.Email, "auth.login");

        return Ok(new AuthResponse(tokenService.CreateToken(user), ToDto(user)));
    }

    internal static UserDto ToDto(User user) => new(user.Id, user.Email, user.DisplayName, user.Role);
}
