namespace ZPlus.Shared.Dtos;

public record CreateMeetingRequest(
    string Topic,
    string? Password,
    DateTime? ScheduledStartUtc,
    int? DurationMinutes,
    List<string>? InviteEmails = null,
    bool WaitingRoomEnabled = false,
    // Recurrence: "None" | "Daily" | "Weekly" | "Monthly". Count includes the first occurrence.
    string RecurrencePattern = "None",
    int RecurrenceCount = 1,
    // How many minutes before each occurrence to email invitations (0 = send immediately).
    int ReminderLeadMinutes = 0,
    // The zone/format the host scheduled in, so invitations can show their local time as well as UTC.
    // Accepts a Windows ("Central Standard Time") or IANA ("America/Chicago") id.
    string HostTimeZoneId = "",
    bool Use24HourTime = false);

/// <summary>
/// Result of creating a meeting (or a recurring series). <see cref="Meeting"/> is the first
/// occurrence; <see cref="OccurrencesCreated"/> is how many meetings were created.
/// <see cref="InvitesSent"/> counts invitations emailed immediately; the rest are queued as
/// reminders and sent by the server at their scheduled time.
/// </summary>
public record CreateMeetingResponse(
    MeetingDto Meeting,
    int InvitesSent,
    List<string> InviteFailures,
    int OccurrencesCreated = 1,
    int InvitesQueued = 0);

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
