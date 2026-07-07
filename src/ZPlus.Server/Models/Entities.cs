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
    /// <summary>Null when the meeting has no password.</summary>
    public string? PasswordHash { get; set; }
    public bool IsActive { get; set; }
    public DateTime? EndedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public List<MeetingParticipantRecord> Participants { get; set; } = [];
    public List<ChatMessageRecord> ChatMessages { get; set; } = [];
}

/// <summary>An email invitation sent (or attempted) for a meeting.</summary>
public class MeetingInvitation
{
    public long Id { get; set; }
    public Guid MeetingId { get; set; }
    public string Email { get; set; } = "";
    public Guid InvitedByUserId { get; set; }
    public bool Sent { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
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
