namespace ZPlus.Shared.Dtos;

public record CreateMeetingRequest(
    string Topic,
    string? Password,
    DateTime? ScheduledStartUtc,
    int? DurationMinutes,
    List<string>? InviteEmails = null,
    bool WaitingRoomEnabled = false);

/// <summary>Result of creating a meeting, including the outcome of any email invitations.</summary>
public record CreateMeetingResponse(
    MeetingDto Meeting,
    int InvitesSent,
    List<string> InviteFailures);

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
    List<ChatMessageDto> RecentChat,
    // Catch-up state for the feature panels. Defaulted so older call sites stay valid.
    bool InWaitingRoom = false,
    List<MeetingFileDto>? SharedFiles = null,
    PollDto? ActivePoll = null,
    PollResultsDto? ActivePollResults = null,
    List<WhiteboardStrokeDto>? Whiteboard = null,
    List<WaitingParticipantDto>? Waiting = null,
    BreakoutStateDto? Breakouts = null);

public record ChatMessageDto(
    Guid SenderUserId,
    string SenderDisplayName,
    string Text,
    DateTime SentAtUtc,
    bool IsPrivate,
    Guid? RecipientUserId);
