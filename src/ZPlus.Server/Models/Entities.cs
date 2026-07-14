using System.ComponentModel.DataAnnotations.Schema;

namespace ZPlus.Server.Models;

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    /// <summary>One of the ZPlus.Shared.Dtos.Roles constants.</summary>
    public string Role { get; set; } = "User";
    /// <summary>Disabled accounts cannot sign in or join meetings.</summary>
    public bool IsDisabled { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>TOTP shared secret (base32), stored AES+HMAC-encrypted. Null when MFA is off.</summary>
    public string? TotpSecret { get; set; }
    /// <summary>True once the user has completed MFA enrollment.</summary>
    public bool MfaEnabled { get; set; }
    /// <summary>True when an admin has required MFA but the user has not enrolled yet.</summary>
    public bool MfaRequired { get; set; }

    public List<Meeting> HostedMeetings { get; set; } = [];
}

/// <summary>Key/value store for server-wide configuration edited by admins.</summary>
public class ServerSetting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

public class Meeting
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Human-friendly 9-digit code users type to join, e.g. "412-778-903".</summary>
    public string MeetingCode { get; set; } = "";
    public string Topic { get; set; } = "";
    public Guid HostUserId { get; set; }
    public User? Host { get; set; }
    /// <summary>Null for instant meetings.</summary>
    public DateTime? ScheduledStartUtc { get; set; }
    public int? DurationMinutes { get; set; }
    /// <summary>Groups occurrences of a recurring series (legacy; a series is now one meeting).</summary>
    public Guid? SeriesId { get; set; }
    /// <summary>Recurrence for this meeting: "None" | "Daily" | "Weekly" | "Monthly". The same
    /// meeting ID is reused for every occurrence (ending one occurrence does not end the series).</summary>
    public string RecurrencePattern { get; set; } = "None";
    /// <summary>Number of occurrences in the series (1 for a one-off).</summary>
    public int RecurrenceCount { get; set; } = 1;
    /// <summary>When a recurring meeting's ID stops working (just after its last occurrence).
    /// Null for one-off meetings, which never auto-expire.</summary>
    public DateTime? ExpiresAtUtc { get; set; }

    [NotMapped]
    public bool IsRecurring => RecurrencePattern != "None";

    /// <summary>True once the meeting is over: explicitly ended, or past a recurring series' expiry.</summary>
    public bool HasExpired(DateTime nowUtc) =>
        EndedAtUtc is not null || (ExpiresAtUtc is not null && ExpiresAtUtc <= nowUtc);
    /// <summary>Null when the meeting has no password.</summary>
    public string? PasswordHash { get; set; }
    /// <summary>When true, non-host joiners wait for the host to admit them.</summary>
    public bool WaitingRoomEnabled { get; set; }
    public bool IsActive { get; set; }
    public DateTime? EndedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public List<MeetingParticipantRecord> Participants { get; set; } = [];
    public List<ChatMessageRecord> ChatMessages { get; set; } = [];
}

/// <summary>An email invitation for a meeting. May be queued as a timed reminder.</summary>
public class MeetingInvitation
{
    public long Id { get; set; }
    public Guid MeetingId { get; set; }
    public string Email { get; set; } = "";
    public Guid InvitedByUserId { get; set; }
    public bool Sent { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    /// <summary>When the background dispatcher should send this. Null = handled inline at creation.</summary>
    public DateTime? SendAtUtc { get; set; }
    /// <summary>The meeting password (AES+HMAC-encrypted) so a deferred reminder can include a join link.</summary>
    public string? ProtectedPassword { get; set; }
    /// <summary>True when this invite is a reminder for a scheduled occurrence (affects wording).</summary>
    public bool IsReminder { get; set; }
    public string? HostDisplayName { get; set; }
    /// <summary>The specific occurrence this reminder is for, so its email shows the right date.</summary>
    public DateTime? OccurrenceStartUtc { get; set; }
}

/// <summary>Historical attendance record (live roster is kept in memory).</summary>
public class MeetingParticipantRecord
{
    public long Id { get; set; }
    public Guid MeetingId { get; set; }
    public Guid UserId { get; set; }
    public DateTime JoinedAtUtc { get; set; }
    public DateTime? LeftAtUtc { get; set; }
}

public class ChatMessageRecord
{
    public long Id { get; set; }
    public Guid MeetingId { get; set; }
    public Guid SenderUserId { get; set; }
    public string SenderDisplayName { get; set; } = "";
    public string Text { get; set; } = "";
    public bool IsPrivate { get; set; }
    public Guid? RecipientUserId { get; set; }
    public DateTime SentAtUtc { get; set; }
}

/// <summary>A poll created by a host during a meeting. Options are newline-delimited.</summary>
public class Poll
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MeetingId { get; set; }
    public string Question { get; set; } = "";
    /// <summary>Answer options, one per line.</summary>
    public string Options { get; set; } = "";
    public Guid CreatedByUserId { get; set; }
    public bool IsClosed { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public List<PollVote> Votes { get; set; } = [];
}

/// <summary>One participant's vote in a poll (at most one row per user per poll).</summary>
public class PollVote
{
    public long Id { get; set; }
    public Guid PollId { get; set; }
    public Guid UserId { get; set; }
    public int OptionIndex { get; set; }
}

/// <summary>A file shared into a meeting. The bytes live in the database for portability.</summary>
public class MeetingFile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MeetingId { get; set; }
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "application/octet-stream";
    public long Size { get; set; }
    public Guid SenderUserId { get; set; }
    public string SenderDisplayName { get; set; } = "";
    public byte[] Content { get; set; } = [];
    public DateTime SharedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>An append-only record of a security- or administration-relevant action.</summary>
public class AuditLog
{
    public long Id { get; set; }
    public DateTime WhenUtc { get; set; } = DateTime.UtcNow;
    public Guid? ActorUserId { get; set; }
    public string ActorEmail { get; set; } = "";
    public string Action { get; set; } = "";
    public string Details { get; set; } = "";
}
