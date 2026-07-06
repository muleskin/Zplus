using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZPlus.Client.Media;
using ZPlus.Client.Services;
using ZPlus.Shared.Dtos;

namespace ZPlus.Client.ViewModels;

public record ChatRecipientOption(string Label, Guid? UserId);

public partial class MeetingViewModel : ObservableObject
{
    private readonly string _meetingCode;
    private readonly string? _password;
    private readonly MeetingHubClient _hub = new();
    private readonly WebRtcManager _webRtc;

    [ObservableProperty] private string _title = "Connecting…";
    [ObservableProperty] private string _meetingCodeDisplay = "";
    [ObservableProperty] private bool _isMuted;
    [ObservableProperty] private bool _isVideoOn = true;
    [ObservableProperty] private bool _isSelfHost;
    [ObservableProperty] private string _chatText = "";
    [ObservableProperty] private ChatRecipientOption? _selectedRecipient;
    [ObservableProperty] private string? _statusMessage;

    public ObservableCollection<ParticipantTileViewModel> Tiles { get; } = [];
    public ObservableCollection<ChatMessageDto> ChatMessages { get; } = [];
    public ObservableCollection<ChatRecipientOption> ChatRecipients { get; } = [];

    private ParticipantTileViewModel? _selfTile;

    /// <summary>Raised when the meeting is over for this client; the argument explains why (null = user chose to leave).</summary>
    public event Action<string?>? MeetingExited;

    public MeetingViewModel(string meetingCode, string? password)
    {
        _meetingCode = meetingCode;
        _password = password;
        _webRtc = new WebRtcManager(_hub);

        _hub.ParticipantJoined += p => OnUi(() => HandleParticipantJoined(p));
        _hub.ParticipantLeft += p => OnUi(() => HandleParticipantLeft(p));
        _hub.ParticipantStateChanged += p => OnUi(() => FindTile(p.ConnectionId)?.ApplyState(p));
        _hub.ChatReceived += m => OnUi(() => ChatMessages.Add(m));
        _hub.HostChanged += p => OnUi(() => HandleHostChanged(p));
        _hub.ForcedMute += () => OnUi(() => ApplyForcedMute());
        _hub.UnmuteRequested += () => OnUi(() => StatusMessage = "The host asks you to unmute.");
        _hub.RemovedFromMeeting += () => OnUi(() => MeetingExited?.Invoke("You were removed from the meeting by the host."));
        _hub.MeetingEnded += () => OnUi(() => MeetingExited?.Invoke("The host ended the meeting."));
        _hub.ConnectionClosed += _ => OnUi(() => StatusMessage = "Connection lost. Reconnecting…");

        _webRtc.LocalVideoFrame += frame => OnUi(() =>
        {
            if (IsVideoOn) _selfTile?.RenderFrame(frame);
        });
        _webRtc.RemoteVideoFrame += (connectionId, frame) => OnUi(() => FindTile(connectionId)?.RenderFrame(frame));
    }

    public async Task InitializeAsync()
    {
        await _hub.ConnectAsync();
        var snapshot = await _hub.JoinMeetingAsync(_meetingCode, _password);

        Title = snapshot.Meeting.Topic;
        MeetingCodeDisplay = $"Meeting ID: {snapshot.Meeting.MeetingCode}";
        IsSelfHost = snapshot.Self.IsHost;

        _selfTile = ParticipantTileViewModel.From(snapshot.Self, isSelf: true);
        Tiles.Add(_selfTile);
        foreach (var participant in snapshot.Participants)
        {
            Tiles.Add(ParticipantTileViewModel.From(participant));
        }
        foreach (var message in snapshot.RecentChat)
        {
            ChatMessages.Add(message);
        }
        RebuildChatRecipients();

        // Start local media, then offer to everyone already in the room (mesh convention:
        // the newcomer always initiates).
        try
        {
            await _webRtc.StartMicrophoneAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Microphone unavailable: {ex.Message}";
        }
        try
        {
            await _webRtc.StartCameraAsync();
            await _hub.SetVideoOnAsync(true);
            if (_selfTile is not null) _selfTile.IsVideoOn = true;
        }
        catch (Exception ex)
        {
            IsVideoOn = false;
            StatusMessage = $"Camera unavailable: {ex.Message}";
        }

        foreach (var participant in snapshot.Participants)
        {
            await _webRtc.ConnectToPeerAsync(participant.ConnectionId);
        }
    }

    // ---- Hub event handling ------------------------------------------------

    private void HandleParticipantJoined(ParticipantDto p)
    {
        if (FindTile(p.ConnectionId) is null)
        {
            Tiles.Add(ParticipantTileViewModel.From(p));
        }
        RebuildChatRecipients();
        StatusMessage = $"{p.DisplayName} joined.";
        // The newcomer will send us an offer; nothing to initiate from this side.
    }

    private void HandleParticipantLeft(ParticipantDto p)
    {
        var tile = FindTile(p.ConnectionId);
        if (tile is not null && !tile.IsSelf) Tiles.Remove(tile);
        _ = _webRtc.DisconnectPeerAsync(p.ConnectionId);
        RebuildChatRecipients();
        StatusMessage = $"{p.DisplayName} left.";
    }

    private void HandleHostChanged(ParticipantDto newHost)
    {
        foreach (var tile in Tiles) tile.IsHost = tile.ConnectionId == newHost.ConnectionId;
        IsSelfHost = _selfTile?.ConnectionId == newHost.ConnectionId;
        StatusMessage = $"{newHost.DisplayName} is now the host.";
    }

    private void ApplyForcedMute()
    {
        IsMuted = true;
        _webRtc.SetMicEnabled(false);
        if (_selfTile is not null) _selfTile.IsMuted = true;
        StatusMessage = "The host muted you.";
    }

    // ---- Commands ------------------------------------------------------------

    [RelayCommand]
    private async Task ToggleMuteAsync()
    {
        IsMuted = !IsMuted;
        _webRtc.SetMicEnabled(!IsMuted);
        if (_selfTile is not null) _selfTile.IsMuted = IsMuted;
        await _hub.SetMutedAsync(IsMuted);
    }

    [RelayCommand]
    private async Task ToggleVideoAsync()
    {
        IsVideoOn = !IsVideoOn;
        _webRtc.SetCameraEnabled(IsVideoOn);
        if (_selfTile is not null)
        {
            _selfTile.IsVideoOn = IsVideoOn;
            if (!IsVideoOn) _selfTile.ClearVideo();
        }
        await _hub.SetVideoOnAsync(IsVideoOn);
    }

    [RelayCommand]
    private async Task SendChatAsync()
    {
        var text = ChatText.Trim();
        if (text.Length == 0) return;
        ChatText = "";
        try
        {
            await _hub.SendChatAsync(text, SelectedRecipient?.UserId);
        }
        catch (Exception)
        {
            StatusMessage = "Message failed to send.";
        }
    }

    [RelayCommand]
    private async Task LeaveAsync()
    {
        try { await _hub.LeaveMeetingAsync(); } catch { /* connection may already be gone */ }
        MeetingExited?.Invoke(null);
    }

    [RelayCommand]
    private async Task EndForAllAsync()
    {
        try { await _hub.EndMeetingForAllAsync(); } catch { /* server will notify via MeetingEnded */ }
    }

    [RelayCommand]
    private async Task MuteAllAsync()
    {
        try { await _hub.MuteAllAsync(); } catch { StatusMessage = "Mute all failed."; }
    }

    [RelayCommand]
    private async Task AskToUnmuteAsync(ParticipantTileViewModel? tile)
    {
        if (tile is null || tile.IsSelf) return;
        try { await _hub.AskToUnmuteAsync(tile.ConnectionId); } catch { /* best effort */ }
    }

    [RelayCommand]
    private async Task RemoveParticipantAsync(ParticipantTileViewModel? tile)
    {
        if (tile is null || tile.IsSelf) return;
        try { await _hub.RemoveParticipantAsync(tile.ConnectionId); } catch { StatusMessage = "Remove failed."; }
    }

    [RelayCommand]
    private async Task TransferHostAsync(ParticipantTileViewModel? tile)
    {
        if (tile is null || tile.IsSelf) return;
        try { await _hub.TransferHostAsync(tile.ConnectionId); } catch { StatusMessage = "Host transfer failed."; }
    }

    // ---- Teardown --------------------------------------------------------------

    public async Task ShutdownAsync()
    {
        await _webRtc.DisposeAsync();
        await _hub.DisposeAsync();
    }

    // ---- Helpers -----------------------------------------------------------------

    private ParticipantTileViewModel? FindTile(string connectionId) =>
        Tiles.FirstOrDefault(t => t.ConnectionId == connectionId);

    private void RebuildChatRecipients()
    {
        var previous = SelectedRecipient;
        ChatRecipients.Clear();
        ChatRecipients.Add(new ChatRecipientOption("Everyone", null));
        foreach (var tile in Tiles.Where(t => !t.IsSelf))
        {
            ChatRecipients.Add(new ChatRecipientOption($"{tile.DisplayName} (privately)", tile.UserId));
        }
        SelectedRecipient = ChatRecipients.FirstOrDefault(r => r.UserId == previous?.UserId) ?? ChatRecipients[0];
    }

    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }
}
