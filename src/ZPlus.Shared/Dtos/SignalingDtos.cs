namespace ZPlus.Shared.Dtos;

/// <summary>
/// WebRTC signaling envelope relayed verbatim between two peers by the server.
/// Type is one of "offer", "answer" or "ice"; Payload is the SDP or ICE candidate JSON.
/// </summary>
public record SignalMessage(string FromConnectionId, string Type, string Payload);

/// <summary>Names of hub methods the client invokes on the server.</summary>
public static class HubMethods
{
    public const string JoinMeeting = nameof(JoinMeeting);
    public const string LeaveMeeting = nameof(LeaveMeeting);
    public const string SendChat = nameof(SendChat);
    public const string SendSignal = nameof(SendSignal);
    public const string SetMuted = nameof(SetMuted);
    public const string SetVideoOn = nameof(SetVideoOn);
    public const string MuteAll = nameof(MuteAll);
    public const string AskToUnmute = nameof(AskToUnmute);
    public const string RemoveParticipant = nameof(RemoveParticipant);
    public const string TransferHost = nameof(TransferHost);
    public const string EndMeetingForAll = nameof(EndMeetingForAll);

    // Reactions
    public const string SendReaction = nameof(SendReaction);
    // Waiting room (host admits/denies)
    public const string AdmitParticipant = nameof(AdmitParticipant);
    public const string DenyParticipant = nameof(DenyParticipant);
    // Polls
    public const string CreatePoll = nameof(CreatePoll);
    public const string VotePoll = nameof(VotePoll);
    public const string ClosePoll = nameof(ClosePoll);
    // File sharing (announce a file already uploaded via REST)
    public const string ShareFile = nameof(ShareFile);
    // Whiteboard
    public const string WhiteboardDraw = nameof(WhiteboardDraw);
    public const string WhiteboardClear = nameof(WhiteboardClear);
    // Breakout rooms
    public const string CreateBreakoutRooms = nameof(CreateBreakoutRooms);
    public const string AssignBreakout = nameof(AssignBreakout);
    public const string OpenBreakouts = nameof(OpenBreakouts);
    public const string CloseBreakouts = nameof(CloseBreakouts);
}

/// <summary>Names of hub events the server raises on clients.</summary>
public static class HubEvents
{
    public const string ParticipantJoined = nameof(ParticipantJoined);
    public const string ParticipantLeft = nameof(ParticipantLeft);
    public const string ParticipantStateChanged = nameof(ParticipantStateChanged);
    public const string ChatReceived = nameof(ChatReceived);
    public const string SignalReceived = nameof(SignalReceived);
    public const string HostChanged = nameof(HostChanged);
    public const string ForcedMute = nameof(ForcedMute);
    public const string UnmuteRequested = nameof(UnmuteRequested);
    public const string RemovedFromMeeting = nameof(RemovedFromMeeting);
    public const string MeetingEnded = nameof(MeetingEnded);

    // Reactions
    public const string ReactionReceived = nameof(ReactionReceived);
    // Waiting room
    public const string ParticipantWaiting = nameof(ParticipantWaiting);  // -> host: someone is waiting
    public const string WaitingCleared = nameof(WaitingCleared);          // -> host: a waiter left/was handled
    public const string AdmittedToMeeting = nameof(AdmittedToMeeting);    // -> waiter: here is your snapshot
    public const string WaitingDenied = nameof(WaitingDenied);            // -> waiter: host declined
    // Polls
    public const string PollStarted = nameof(PollStarted);
    public const string PollUpdated = nameof(PollUpdated);
    public const string PollClosed = nameof(PollClosed);
    // File sharing
    public const string FileShared = nameof(FileShared);
    // Whiteboard
    public const string WhiteboardStrokeReceived = nameof(WhiteboardStrokeReceived);
    public const string WhiteboardCleared = nameof(WhiteboardCleared);
    // Breakout rooms
    public const string BreakoutsUpdated = nameof(BreakoutsUpdated);      // roster/assignments changed
    public const string BreakoutAssigned = nameof(BreakoutAssigned);      // -> a participant: your room
    public const string BreakoutsOpened = nameof(BreakoutsOpened);
    public const string BreakoutsClosed = nameof(BreakoutsClosed);
}
