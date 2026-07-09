namespace ZPlus.Shared.Dtos;

// ---- Reactions ----------------------------------------------------------------

/// <summary>A transient emoji reaction sent by a participant.</summary>
public record ReactionDto(Guid UserId, string DisplayName, string Emoji);

// ---- Waiting room -------------------------------------------------------------

/// <summary>Someone sitting in the waiting room, shown to the host for admit/deny.</summary>
public record WaitingParticipantDto(string ConnectionId, Guid UserId, string DisplayName);

// ---- Polls --------------------------------------------------------------------

/// <summary>A poll definition broadcast when it starts (or replayed on join).</summary>
public record PollDto(Guid PollId, string Question, List<string> Options, bool IsClosed);

/// <summary>Live tallies for a poll; index i pairs with PollDto.Options[i].</summary>
public record PollResultsDto(Guid PollId, List<int> Votes, int TotalVotes, bool IsClosed);

// ---- File sharing -------------------------------------------------------------

/// <summary>
/// Metadata for a file shared into a meeting. DownloadPath is server-relative
/// (e.g. "/api/files/{id}"); the client resolves it against its server URL.
/// </summary>
public record MeetingFileDto(
    Guid FileId,
    string FileName,
    long Size,
    string ContentType,
    Guid SenderUserId,
    string SenderDisplayName,
    DateTime SharedAtUtc,
    string DownloadPath);

/// <summary>Response from the file upload endpoint before the file is announced to the meeting.</summary>
public record FileUploadResponse(Guid FileId, string FileName, long Size, string ContentType, string DownloadPath);

// ---- Whiteboard ---------------------------------------------------------------

/// <summary>
/// One freehand stroke on the shared whiteboard. Points are normalized 0..1 coordinates
/// (x0,y0,x1,y1,...) so the drawing scales across differently sized canvases.
/// </summary>
public record WhiteboardStrokeDto(string Color, double Width, List<double> Points);

// ---- Breakout rooms -----------------------------------------------------------

public record BreakoutRoomDto(int Index, string Name, List<WaitingParticipantDto> Members);

/// <summary>The full breakout picture broadcast to the host (and, when open, to everyone).</summary>
public record BreakoutStateDto(List<BreakoutRoomDto> Rooms, bool IsOpen);
