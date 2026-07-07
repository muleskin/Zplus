namespace ZPlus.Shared.Dtos;

/// <summary>Role names used in JWT role claims and [Authorize] policies.</summary>
public static class Roles
{
    public const string User = "User";
    public const string Admin = "Admin";
    public const string SuperAdmin = "SuperAdmin";

    public static readonly string[] All = [User, Admin, SuperAdmin];
}

public record AdminUserDto(
    Guid Id,
    string Email,
    string DisplayName,
    string Role,
    bool IsDisabled,
    DateTime CreatedAtUtc);

public record AdminCreateUserRequest(string Email, string DisplayName, string Password, string Role);

/// <summary>Null fields are left unchanged.</summary>
public record AdminUpdateUserRequest(string? DisplayName, string? Role, bool? IsDisabled);

public record AdminResetPasswordRequest(string NewPassword);

/// <summary>
/// Server-wide configuration, stored in the database and editable from the admin app.
/// SmtpPassword is write-only: reads always return "" and saving an empty value keeps
/// the stored password; PublicUrl is the externally reachable address used in invite links.
/// </summary>
public record ServerSettingsDto(
    bool AllowSelfRegistration,
    bool RequireMeetingPasswords,
    int MaxParticipantsPerMeeting,
    string ListenUrl,
    string PublicUrl = "",
    string SmtpHost = "",
    int SmtpPort = 587,
    string SmtpFrom = "",
    string SmtpUser = "",
    string SmtpPassword = "");

/// <summary>Sends a test email using the given settings (blank password uses the stored one).</summary>
public record SmtpTestRequest(ServerSettingsDto Settings, string Recipient);

public record ActiveMeetingDto(
    Guid Id,
    string MeetingCode,
    string Topic,
    string HostDisplayName,
    int ParticipantCount,
    DateTime CreatedAtUtc);
