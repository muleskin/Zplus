namespace ZPlus.Shared.Dtos;

public record RegisterRequest(string Email, string DisplayName, string Password);

/// <summary>
/// Sign-in request. When the account has (or is being required to add) MFA, the client
/// re-sends the same request with a 6-digit <see cref="MfaCode"/>. During first-time
/// enrollment it also echoes back the <see cref="MfaEnrollSecret"/> it was handed.
/// </summary>
public record LoginRequest(string Email, string Password, string? MfaCode = null, string? MfaEnrollSecret = null);

/// <summary>
/// Result of a sign-in. On success <see cref="Token"/> and <see cref="User"/> are set.
/// When MFA is needed, <see cref="MfaRequired"/> is true and <see cref="Enrollment"/> is
/// non-null only the first time (so the user can add the account to an authenticator app).
/// </summary>
public record AuthResponse(
    string? Token,
    UserDto? User,
    bool MfaRequired = false,
    MfaEnrollmentDto? Enrollment = null);

/// <summary>Secret + otpauth:// URI presented once when a user first enrolls in MFA.</summary>
public record MfaEnrollmentDto(string Secret, string OtpauthUri, string Issuer, string Account);

public record UserDto(Guid Id, string Email, string DisplayName, string Role);
