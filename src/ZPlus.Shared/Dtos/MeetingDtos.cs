namespace ZPlus.Shared.Dtos;

public record CreateMeetingRequest(
    string Topic,
    string? Password,
    DateTime? ScheduledStartUtc,
    int? DurationMinutes);

public record JoinLookupRequest(string MeetingCode, string? Password);

public record MeetingDto(
    Guid Id,
    string MeetingCode,
    string Topic,
    Guid HostUserId,
    string HostDisplayName,
    DateTime? ScheduledStartUtc,
    int? DurationMinutes,
    bool RequiresPassword,
    bool IsActive,
    DateTime CreatedAtUtc);

public record ParticipantDto(
    Guid UserId,
    string ConnectionId,
    string DisplayName,
    bool IsHost,
    bool IsMuted,
    bool IsVideoOn);

/// <summary>Snapshot returned to a client immediately after it joins a meeting.</summary>
public record MeetingJoinedSnapshot(
    MeetingDto Meeting,
    ParticipantDto Self,
    List<ParticipantDto> Participants,
    List<ChatMessageDto> RecentChat);

public record ChatMessageDto(
    Guid SenderUserId,
    string SenderDisplayName,
    string Text,
    DateTime SentAtUtc,
    bool IsPrivate,
    Guid? RecipientUserId);
