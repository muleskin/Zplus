using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZPlus.Client.Services;
using ZPlus.Shared.Dtos;
using ZPlus.Shared.E2ee;

namespace ZPlus.ClientGui.ViewModels;

public partial class ParticipantItem : ObservableObject
{
    [ObservableProperty] private bool _isHost;
    [ObservableProperty] private bool _isMuted;

    public string ConnectionId { get; init; } = "";
    public Guid UserId { get; init; }
    public string DisplayName { get; init; } = "";
    public bool IsSelf { get; init; }

    public static ParticipantItem From(ParticipantDto dto, bool isSelf = false) => new()
    {
        ConnectionId = dto.ConnectionId,
        UserId = dto.UserId,
        DisplayName = isSelf ? $"{dto.DisplayName} (You)" : dto.DisplayName,
        IsSelf = isSelf,
        IsHost = dto.IsHost,
        IsMuted = dto.IsMuted,
    };
}

public record ChatLine(string Sender, string Text, bool IsPrivate, string Time);

public record ChatRecipientOption(string Label, Guid? UserId);

/// <summary>
/// Meeting session for the cross-platform GUI client: live roster, end-to-end encrypted
/// chat, and host controls. Audio/video capture is not available off-Windows yet.
/// </summary>
public partial class MeetingViewModel : ObservableObject
{
    private readonly string _meetingCode;
    private readonly string? _password;
    private readonly MeetingHubClient _hub = new();
    private readonly MeetingE2ee _e2ee = new();
    private readonly List<ChatMessageDto> _pendingEncrypted = [];
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;

    [ObservableProperty] private string _title = "Connecting…";
    [ObservableProperty] private string _meetingCodeDisplay = "";
    [ObservableProperty] private bool _isSelfHost;
    [ObservableProperty] private bool _isE2eeActive;
    [ObservableProperty] private string _chatText = "";
    [ObservableProperty] private ChatRecipientOption? _selectedRecipient;
    [ObservableProperty] private string? _statusMessage;

    public ObservableCollection<ParticipantItem> Participants { get; } = [];
    public ObservableCollection<ChatLine> ChatMessages { get; } = [];
    public ObservableCollection<ChatRecipientOption> ChatRecipients { get; } = [];

    private ParticipantItem? _self;

    /// <summary>Raised when the session is over; the argument explains why (null = user chose to leave).</summary>
    public event Action<string?>? MeetingExited;

    public MeetingViewModel(string meetingCode, string? password)
    {
        _meetingCode = meetingCode;
        _password = password;

        _hub.ParticipantJoined += p => OnUi(() => HandleJoined(p));
        _hub.ParticipantLeft += p => OnUi(() => HandleLeft(p));
        _hub.ParticipantStateChanged += p => OnUi(() => Apply(p));
        _hub.ChatReceived += m => OnUi(() => HandleChat(m));
        _hub.HostChanged += p => OnUi(() => HandleHostChanged(p));
        _hub.ForcedMute += () => OnUi(() => StatusMessage = "The host muted you.");
        _hub.UnmuteRequested += () => OnUi(() => StatusMessage = "The host asks you to unmute.");
        _hub.RemovedFromMeeting += () => OnUi(() => MeetingExited?.Invoke("You were removed from the meeting by the host."));
        _hub.MeetingEnded += () => OnUi(() => MeetingExited?.Invoke("The host ended the meeting."));
        _hub.SignalReceived += s => OnUi(() => HandleSignal(s));
    }

    public async Task InitializeAsync()
    {
        await _hub.ConnectAsync();
        var snapshot = await _hub.JoinMeetingAsync(_meetingCode, _password);

        Title = snapshot.Meeting.Topic;
        MeetingCodeDisplay = $"Meeting ID: {snapshot.Meeting.MeetingCode}";
        IsSelfHost = snapshot.Self.IsHost;

        _self = ParticipantItem.From(snapshot.Self, isSelf: true);
        Participants.Add(_self);
        foreach (var p in snapshot.Participants) Participants.Add(ParticipantItem.From(p));
        RebuildRecipients();

        if (snapshot.Participants.Count == 0)
        {
            _e2ee.CreateMeetingKey();
            IsE2eeActive = true;
        }
        else
        {
            var keyHolder = snapshot.Participants.FirstOrDefault(p => p.IsHost) ?? snapshot.Participants[0];
            await _hub.SendSignalAsync(keyHolder.ConnectionId, MeetingE2ee.SignalPublicKey, _e2ee.PublicKeyBase64);
        }

        foreach (var message in snapshot.RecentChat) HandleChat(message);
    }

    // ---- hub events ---------------------------------------------------------

    private void HandleJoined(ParticipantDto p)
    {
        if (Participants.All(x => x.ConnectionId != p.ConnectionId))
            Participants.Add(ParticipantItem.From(p));
        RebuildRecipients();
        StatusMessage = $"{p.DisplayName} joined.";
    }

    private void HandleLeft(ParticipantDto p)
    {
        var item = Participants.FirstOrDefault(x => x.ConnectionId == p.ConnectionId);
        if (item is not null && !item.IsSelf) Participants.Remove(item);
        RebuildRecipients();
        StatusMessage = $"{p.DisplayName} left.";
    }

    private void Apply(ParticipantDto p)
    {
        var item = Participants.FirstOrDefault(x => x.ConnectionId == p.ConnectionId);
        if (item is null) return;
        item.IsHost = p.IsHost;
        item.IsMuted = p.IsMuted;
    }

    private void HandleHostChanged(ParticipantDto newHost)
    {
        foreach (var p in Participants) p.IsHost = p.ConnectionId == newHost.ConnectionId;
        IsSelfHost = _self?.ConnectionId == newHost.ConnectionId;
        StatusMessage = $"{newHost.DisplayName} is now the host.";
        if (!_e2ee.HasKey && !IsSelfHost)
            _ = _hub.SendSignalAsync(newHost.ConnectionId, MeetingE2ee.SignalPublicKey, _e2ee.PublicKeyBase64);
    }

    private void HandleSignal(SignalMessage signal)
    {
        switch (signal.Type)
        {
            case MeetingE2ee.SignalPublicKey when _e2ee.HasKey:
                _ = _hub.SendSignalAsync(signal.FromConnectionId, MeetingE2ee.SignalWrappedKey,
                    _e2ee.WrapKeyFor(signal.Payload));
                break;
            case MeetingE2ee.SignalWrappedKey when !_e2ee.HasKey:
                if (_e2ee.TryUnwrapKey(signal.Payload))
                {
                    IsE2eeActive = true;
                    foreach (var pending in _pendingEncrypted) HandleChat(pending);
                    _pendingEncrypted.Clear();
                }
                break;
        }
    }

    private void HandleChat(ChatMessageDto message)
    {
        if (_e2ee.TryDecrypt(message.Text, out var plaintext))
            ChatMessages.Add(new ChatLine(message.SenderDisplayName, plaintext, message.IsPrivate,
                message.SentAtUtc.ToLocalTime().ToString("t")));
        else if (MeetingE2ee.IsEncrypted(message.Text))
        {
            if (!_e2ee.HasKey) _pendingEncrypted.Add(message);
        }
        else
            ChatMessages.Add(new ChatLine(message.SenderDisplayName, message.Text, message.IsPrivate,
                message.SentAtUtc.ToLocalTime().ToString("t")));
    }

    // ---- commands -------------------------------------------------------------

    [RelayCommand]
    private async Task SendChatAsync()
    {
        var text = ChatText.Trim();
        if (text.Length == 0) return;
        if (text.Length > 2000) text = text[..2000];
        if (!_e2ee.HasKey)
        {
            StatusMessage = "Securing chat — try again in a moment.";
            return;
        }
        ChatText = "";
        try
        {
            await _hub.SendChatAsync(_e2ee.Encrypt(text), SelectedRecipient?.UserId);
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
        try { await _hub.EndMeetingForAllAsync(); } catch { /* server will notify */ }
    }

    [RelayCommand]
    private async Task MuteAllAsync()
    {
        try { await _hub.MuteAllAsync(); } catch { StatusMessage = "Mute all failed."; }
    }

    [RelayCommand]
    private async Task AskToUnmuteAsync(ParticipantItem? item)
    {
        if (item is null || item.IsSelf) return;
        try { await _hub.AskToUnmuteAsync(item.ConnectionId); } catch { /* best effort */ }
    }

    [RelayCommand]
    private async Task RemoveParticipantAsync(ParticipantItem? item)
    {
        if (item is null || item.IsSelf) return;
        try { await _hub.RemoveParticipantAsync(item.ConnectionId); } catch { StatusMessage = "Remove failed."; }
    }

    [RelayCommand]
    private async Task TransferHostAsync(ParticipantItem? item)
    {
        if (item is null || item.IsSelf) return;
        try { await _hub.TransferHostAsync(item.ConnectionId); } catch { StatusMessage = "Host transfer failed."; }
    }

    public async Task ShutdownAsync()
    {
        await _hub.DisposeAsync();
        _e2ee.Dispose();
    }

    // ---- helpers ------------------------------------------------------------------

    private void RebuildRecipients()
    {
        var previous = SelectedRecipient;
        ChatRecipients.Clear();
        ChatRecipients.Add(new ChatRecipientOption("Everyone", null));
        foreach (var p in Participants.Where(p => !p.IsSelf))
            ChatRecipients.Add(new ChatRecipientOption($"{p.DisplayName} (privately)", p.UserId));
        SelectedRecipient = ChatRecipients.FirstOrDefault(r => r.UserId == previous?.UserId) ?? ChatRecipients[0];
    }

    private void OnUi(Action action)
    {
        if (_ui is null) action();
        else _ui.Post(_ => action(), null);
    }
}
