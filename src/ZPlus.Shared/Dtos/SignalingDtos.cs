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
}
