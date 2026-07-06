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

    public async ValueTask DisposeAsync()
    {
        try { await _connection.StopAsync(); } catch { /* already disconnected */ }
        await _connection.DisposeAsync();
    }
}
