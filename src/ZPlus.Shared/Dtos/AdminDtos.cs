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
    DateTime CreatedAtUtc,
    bool MfaEnabled = false,
    bool MfaRequired = false);

public record AdminCreateUserRequest(string Email, string DisplayName, string Password, string Role);

/// <summary>Null fields are left unchanged.</summary>
public record AdminUpdateUserRequest(string? DisplayName, string? Role, bool? IsDisabled);

public record AdminResetPasswordRequest(string NewPassword);

/// <summary>
/// Server-wide configuration, stored in the database and editable from the admin app.
/// Email can be sent via SMTP or the Mailgun HTTP API (EmailProvider = "SMTP" | "Mailgun").
/// SmtpPassword and MailgunApiKey are write-only: reads always return "" and saving an
/// empty value keeps the stored secret. PublicUrl is the externally reachable address used
/// in invite links; SmtpFrom is the shared From address for whichever provider is active.
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
    string SmtpPassword = "",
    string EmailProvider = "SMTP",
    string MailgunDomain = "",
    string MailgunRegion = "us",
    string MailgunApiKey = "",
    string CertPath = "",
    string CertPassword = "",
    string CertKeyPath = "");

/// <summary>Sends a test email using the given settings (blank password uses the stored one).</summary>
public record SmtpTestRequest(ServerSettingsDto Settings, string Recipient);

public record ActiveMeetingDto(
    Guid Id,
    string MeetingCode,
    string Topic,
    string HostDisplayName,
    int ParticipantCount,
    DateTime CreatedAtUtc);

// ---- Dashboard ---------------------------------------------------------------

/// <summary>At-a-glance server statistics for the admin dashboard.</summary>
public record DashboardStatsDto(
    int TotalUsers,
    int Admins,
    int DisabledUsers,
    int MfaEnabledUsers,
    int ActiveMeetings,
    int ActiveParticipants,
    int MeetingsToday,
    int MeetingsTotal,
    int MessagesTotal);

// ---- Audit log ---------------------------------------------------------------

public record AuditLogDto(
    long Id,
    DateTime WhenUtc,
    string ActorEmail,
    string Action,
    string Details);

// ---- Smart search ------------------------------------------------------------

public record AdminMeetingDto(
    Guid Id,
    string MeetingCode,
    string Topic,
    string HostDisplayName,
    bool IsActive,
    DateTime CreatedAtUtc,
    DateTime? EndedAtUtc);

/// <summary>Combined result of a smart search across users and meetings.</summary>
public record SearchResultsDto(List<AdminUserDto> Users, List<AdminMeetingDto> Meetings);
