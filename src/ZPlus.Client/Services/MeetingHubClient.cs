using Microsoft.AspNetCore.SignalR.Client;
using ZPlus.Shared.Dtos;

namespace ZPlus.Client.Services;

/// <summary>
/// Typed wrapper around the SignalR meeting hub connection. Events are raised on
/// hub worker threads; subscribers must marshal to the UI thread themselves.
/// </summary>
public class MeetingHubClient : IAsyncDisposable
{
    private readonly HubConnection _connection;

    public event Action<ParticipantDto>? ParticipantJoined;
    public event Action<ParticipantDto>? ParticipantLeft;
    public event Action<ParticipantDto>? ParticipantStateChanged;
    public event Action<ChatMessageDto>? ChatReceived;
    public event Action<SignalMessage>? SignalReceived;
    public event Action<ParticipantDto>? HostChanged;
    public event Action? ForcedMute;
    public event Action? UnmuteRequested;
    public event Action? RemovedFromMeeting;
    public event Action? MeetingEnded;
    public event Action<Exception?>? ConnectionClosed;

    // Reactions
    public event Action<ReactionDto>? ReactionReceived;
    // Waiting room
    public event Action<WaitingParticipantDto>? ParticipantWaiting;
    public event Action<string>? WaitingCleared;
    public event Action<MeetingJoinedSnapshot>? AdmittedToMeeting;
    public event Action<string>? WaitingDenied;
    // Polls
    public event Action<PollDto>? PollStarted;
    public event Action<PollResultsDto>? PollUpdated;
    public event Action<Guid>? PollClosed;
    // File sharing
    public event Action<MeetingFileDto>? FileShared;
    // Whiteboard
    public event Action<WhiteboardStrokeDto>? WhiteboardStrokeReceived;
    public event Action? WhiteboardCleared;
    // Breakout rooms
    public event Action<BreakoutStateDto>? BreakoutsUpdated;
    public event Action<int, string>? BreakoutAssigned;
    public event Action? BreakoutsOpened;
    public event Action? BreakoutsClosed;

    public string? ConnectionId => _connection.ConnectionId;

    public MeetingHubClient()
    {
        var session = AppSession.Current;
        _connection = new HubConnectionBuilder()
            .WithUrl($"{session.ServerUrl.TrimEnd('/')}/hubs/meeting", options =>
            {
                options.AccessTokenProvider = () => Task.FromResult(session.Token);
            })
            .WithAutomaticReconnect()
            .Build();

        _connection.On<ParticipantDto>(HubEvents.ParticipantJoined, p => ParticipantJoined?.Invoke(p));
        _connection.On<ParticipantDto>(HubEvents.ParticipantLeft, p => ParticipantLeft?.Invoke(p));
        _connection.On<ParticipantDto>(HubEvents.ParticipantStateChanged, p => ParticipantStateChanged?.Invoke(p));
        _connection.On<ChatMessageDto>(HubEvents.ChatReceived, m => ChatReceived?.Invoke(m));
        _connection.On<SignalMessage>(HubEvents.SignalReceived, s => SignalReceived?.Invoke(s));
        _connection.On<ParticipantDto>(HubEvents.HostChanged, p => HostChanged?.Invoke(p));
        _connection.On(HubEvents.ForcedMute, () => ForcedMute?.Invoke());
        _connection.On(HubEvents.UnmuteRequested, () => UnmuteRequested?.Invoke());
        _connection.On(HubEvents.RemovedFromMeeting, () => RemovedFromMeeting?.Invoke());
        _connection.On(HubEvents.MeetingEnded, () => MeetingEnded?.Invoke());
        _connection.On<ReactionDto>(HubEvents.ReactionReceived, r => ReactionReceived?.Invoke(r));
        _connection.On<WaitingParticipantDto>(HubEvents.ParticipantWaiting, w => ParticipantWaiting?.Invoke(w));
        _connection.On<string>(HubEvents.WaitingCleared, c => WaitingCleared?.Invoke(c));
        _connection.On<MeetingJoinedSnapshot>(HubEvents.AdmittedToMeeting, s => AdmittedToMeeting?.Invoke(s));
        _connection.On<string>(HubEvents.WaitingDenied, r => WaitingDenied?.Invoke(r));
        _connection.On<PollDto>(HubEvents.PollStarted, p => PollStarted?.Invoke(p));
        _connection.On<PollResultsDto>(HubEvents.PollUpdated, r => PollUpdated?.Invoke(r));
        _connection.On<Guid>(HubEvents.PollClosed, id => PollClosed?.Invoke(id));
        _connection.On<MeetingFileDto>(HubEvents.FileShared, f => FileShared?.Invoke(f));
        _connection.On<WhiteboardStrokeDto>(HubEvents.WhiteboardStrokeReceived, s => WhiteboardStrokeReceived?.Invoke(s));
        _connection.On(HubEvents.WhiteboardCleared, () => WhiteboardCleared?.Invoke());
        _connection.On<BreakoutStateDto>(HubEvents.BreakoutsUpdated, s => BreakoutsUpdated?.Invoke(s));
        _connection.On<int, string>(HubEvents.BreakoutAssigned, (i, n) => BreakoutAssigned?.Invoke(i, n));
        _connection.On(HubEvents.BreakoutsOpened, () => BreakoutsOpened?.Invoke());
        _connection.On(HubEvents.BreakoutsClosed, () => BreakoutsClosed?.Invoke());
        _connection.Closed += ex => { ConnectionClosed?.Invoke(ex); return Task.CompletedTask; };
    }

    public Task ConnectAsync() => _connection.StartAsync();

    public Task<MeetingJoinedSnapshot> JoinMeetingAsync(string meetingCode, string? password) =>
        _connection.InvokeAsync<MeetingJoinedSnapshot>(HubMethods.JoinMeeting, meetingCode, password);

    public Task LeaveMeetingAsync() => _connection.InvokeAsync(HubMethods.LeaveMeeting);

    public Task SendChatAsync(string text, Guid? recipientUserId) =>
        _connection.InvokeAsync(HubMethods.SendChat, text, recipientUserId);

    public Task SendSignalAsync(string targetConnectionId, string type, string payload) =>
        _connection.InvokeAsync(HubMethods.SendSignal, targetConnectionId, type, payload);

    public Task SetMutedAsync(bool muted) => _connection.InvokeAsync(HubMethods.SetMuted, muted);

    public Task SetVideoOnAsync(bool videoOn) => _connection.InvokeAsync(HubMethods.SetVideoOn, videoOn);

    public Task MuteAllAsync() => _connection.InvokeAsync(HubMethods.MuteAll);

    public Task AskToUnmuteAsync(string targetConnectionId) =>
        _connection.InvokeAsync(HubMethods.AskToUnmute, targetConnectionId);

    public Task RemoveParticipantAsync(string targetConnectionId) =>
        _connection.InvokeAsync(HubMethods.RemoveParticipant, targetConnectionId);

    public Task TransferHostAsync(string targetConnectionId) =>
        _connection.InvokeAsync(HubMethods.TransferHost, targetConnectionId);

    public Task EndMeetingForAllAsync() => _connection.InvokeAsync(HubMethods.EndMeetingForAll);

    // Reactions
    public Task SendReactionAsync(string emoji) => _connection.InvokeAsync(HubMethods.SendReaction, emoji);

    // Waiting room
    public Task AdmitParticipantAsync(string targetConnectionId) =>
        _connection.InvokeAsync(HubMethods.AdmitParticipant, targetConnectionId);
    public Task DenyParticipantAsync(string targetConnectionId) =>
        _connection.InvokeAsync(HubMethods.DenyParticipant, targetConnectionId);

    // Polls
    public Task CreatePollAsync(string question, List<string> options) =>
        _connection.InvokeAsync(HubMethods.CreatePoll, question, options);
    public Task VotePollAsync(Guid pollId, int optionIndex) =>
        _connection.InvokeAsync(HubMethods.VotePoll, pollId, optionIndex);
    public Task ClosePollAsync(Guid pollId) => _connection.InvokeAsync(HubMethods.ClosePoll, pollId);

    // File sharing (announce a file already uploaded via REST)
    public Task ShareFileAsync(Guid fileId) => _connection.InvokeAsync(HubMethods.ShareFile, fileId);

    // Whiteboard
    public Task WhiteboardDrawAsync(WhiteboardStrokeDto stroke) =>
        _connection.InvokeAsync(HubMethods.WhiteboardDraw, stroke);
    public Task WhiteboardClearAsync() => _connection.InvokeAsync(HubMethods.WhiteboardClear);

    // Breakout rooms
    public Task CreateBreakoutRoomsAsync(int count) =>
        _connection.InvokeAsync(HubMethods.CreateBreakoutRooms, count);
    public Task AssignBreakoutAsync(string targetConnectionId, int roomIndex) =>
        _connection.InvokeAsync(HubMethods.AssignBreakout, targetConnectionId, roomIndex);
    public Task OpenBreakoutsAsync() => _connection.InvokeAsync(HubMethods.OpenBreakouts);
    public Task CloseBreakoutsAsync() => _connection.InvokeAsync(HubMethods.CloseBreakouts);

    public async ValueTask DisposeAsync()
    {
        try { await _connection.StopAsync(); } catch { /* already disconnected */ }
        await _connection.DisposeAsync();
    }
}
