using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZPlus.Client.Media;
using ZPlus.Client.Services;
using ZPlus.Shared.Dtos;
using ZPlus.Shared.E2ee;

namespace ZPlus.Client.ViewModels;

public record ChatRecipientOption(string Label, Guid? UserId);

/// <summary>A transient emoji reaction floating on screen.</summary>
public partial class ReactionItem : ObservableObject
{
    public string Text { get; init; } = "";
}

/// <summary>One poll option row with a live tally.</summary>
public partial class PollOptionVm : ObservableObject
{
    public int Index { get; init; }
    public string Text { get; init; } = "";
    [ObservableProperty] private int _votes;
    [ObservableProperty] private double _fraction;   // 0..1 for the bar width
    [ObservableProperty] private bool _isMyVote;
    public string VotesLabel => Votes == 1 ? "1 vote" : $"{Votes} votes";
    partial void OnVotesChanged(int value) => OnPropertyChanged(nameof(VotesLabel));
}

public partial class MeetingViewModel : ObservableObject
{
    private readonly string _meetingCode;
    private readonly string? _password;
    private readonly MeetingHubClient _hub = new();
    private readonly ApiClient _api = new();
    private Guid _meetingId;
    private readonly WebRtcManager _webRtc;
    private readonly MeetingE2ee _e2ee = new();
    private readonly List<ChatMessageDto> _pendingEncrypted = [];

    [ObservableProperty] private string _title = "Connecting…";
    [ObservableProperty] private string _meetingCodeDisplay = "";
    [ObservableProperty] private bool _isMuted;
    [ObservableProperty] private bool _isVideoOn = true;
    [ObservableProperty] private bool _isSelfHost;
    [ObservableProperty] private bool _isE2eeActive;
    [ObservableProperty] private string _chatText = "";
    [ObservableProperty] private ChatRecipientOption? _selectedRecipient;
    [ObservableProperty] private string? _statusMessage;

    // Waiting room
    [ObservableProperty] private bool _isWaiting;
    [ObservableProperty] private bool _hasWaiting;

    // Polls
    [ObservableProperty] private bool _hasActivePoll;
    [ObservableProperty] private string _pollQuestion = "";
    [ObservableProperty] private bool _pollIsClosed;
    [ObservableProperty] private int _pollTotalVotes;
    [ObservableProperty] private Guid _activePollId;
    // Host poll composer
    [ObservableProperty] private string _newPollQuestion = "";
    [ObservableProperty] private string _newPollOptions = "";

    // Breakouts
    [ObservableProperty] private bool _hasBreakouts;
    [ObservableProperty] private bool _breakoutsOpen;
    [ObservableProperty] private string _myBreakoutRoom = "";
    [ObservableProperty] private string _breakoutRoomCount = "2";

    public ObservableCollection<ParticipantTileViewModel> Tiles { get; } = [];
    public ObservableCollection<ChatMessageDto> ChatMessages { get; } = [];
    public ObservableCollection<ChatRecipientOption> ChatRecipients { get; } = [];
    public ObservableCollection<ReactionItem> Reactions { get; } = [];
    public ObservableCollection<WaitingParticipantDto> Waiting { get; } = [];
    public ObservableCollection<MeetingFileDto> SharedFiles { get; } = [];
    public ObservableCollection<PollOptionVm> PollOptions { get; } = [];
    public ObservableCollection<BreakoutRoomDto> BreakoutRooms { get; } = [];

    public string[] ReactionEmojis => ["👍", "👏", "❤️", "😂", "🎉", "✋"];

    /// <summary>The view supplies these: pick a file to upload / a save path for a download.</summary>
    public Func<Task<string?>>? PickFileToUpload { get; set; }
    public Func<string, Task<string?>>? PickSaveLocation { get; set; }

    /// <summary>Raised for the whiteboard view: a stroke arrived, or the board was cleared.</summary>
    public event Action<WhiteboardStrokeDto>? WhiteboardStrokeReceived;
    public event Action? WhiteboardCleared;
    /// <summary>Strokes present when we joined, for the view to replay once it's ready.</summary>
    public List<WhiteboardStrokeDto> InitialWhiteboard { get; } = [];

    private ParticipantTileViewModel? _selfTile;
    private Guid _selfUserId;

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
        _hub.ChatReceived += m => OnUi(() => HandleChatReceived(m));
        _hub.HostChanged += p => OnUi(() => HandleHostChanged(p));
        _hub.SignalReceived += s => OnUi(() => HandleE2eeSignal(s));
        _hub.ForcedMute += () => OnUi(() => ApplyForcedMute());
        _hub.UnmuteRequested += () => OnUi(() => StatusMessage = "The host asks you to unmute.");
        _hub.RemovedFromMeeting += () => OnUi(() => MeetingExited?.Invoke("You were removed from the meeting by the host."));
        _hub.MeetingEnded += () => OnUi(() => MeetingExited?.Invoke("The host ended the meeting."));
        _hub.ConnectionClosed += _ => OnUi(() => StatusMessage = "Connection lost. Reconnecting…");

        // Feature events
        _hub.ReactionReceived += r => OnUi(() => ShowReaction(r));
        _hub.ParticipantWaiting += w => OnUi(() => { if (Waiting.All(x => x.ConnectionId != w.ConnectionId)) Waiting.Add(w); HasWaiting = Waiting.Count > 0; });
        _hub.WaitingCleared += id => OnUi(() => { var m = Waiting.FirstOrDefault(x => x.ConnectionId == id); if (m is not null) Waiting.Remove(m); HasWaiting = Waiting.Count > 0; });
        _hub.AdmittedToMeeting += s => OnUi(() => { IsWaiting = false; _ = PopulateFromSnapshotAsync(s); });
        _hub.WaitingDenied += reason => OnUi(() => MeetingExited?.Invoke(reason));
        _hub.PollStarted += p => OnUi(() => ApplyPollStarted(p));
        _hub.PollUpdated += r => OnUi(() => ApplyPollResults(r));
        _hub.PollClosed += _ => OnUi(() => PollIsClosed = true);
        _hub.FileShared += f => OnUi(() => { if (SharedFiles.All(x => x.FileId != f.FileId)) SharedFiles.Add(f); StatusMessage = $"{f.SenderDisplayName} shared {f.FileName}."; });
        _hub.WhiteboardStrokeReceived += s => OnUi(() => WhiteboardStrokeReceived?.Invoke(s));
        _hub.WhiteboardCleared += () => OnUi(() => WhiteboardCleared?.Invoke());
        _hub.BreakoutsUpdated += s => OnUi(() => ApplyBreakouts(s));
        _hub.BreakoutAssigned += (room, name) => OnUi(() => { MyBreakoutRoom = room >= 0 ? name : ""; if (room >= 0) StatusMessage = $"You are in {name}."; });
        _hub.BreakoutsOpened += () => OnUi(() => { BreakoutsOpen = true; StatusMessage = "Breakout rooms are open."; });
        _hub.BreakoutsClosed += () => OnUi(() => { BreakoutsOpen = false; MyBreakoutRoom = ""; StatusMessage = "Breakout rooms closed."; });

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

        if (snapshot.InWaitingRoom)
        {
            IsWaiting = true;
            StatusMessage = "Waiting for the host to admit you…";
            return;
        }

        await PopulateFromSnapshotAsync(snapshot);
    }

    /// <summary>Populates the meeting from a join snapshot — used both on direct join and after admission.</summary>
    private async Task PopulateFromSnapshotAsync(MeetingJoinedSnapshot snapshot)
    {
        Title = snapshot.Meeting.Topic;
        MeetingCodeDisplay = $"Meeting ID: {snapshot.Meeting.MeetingCode}";
        IsSelfHost = snapshot.Self.IsHost;
        _selfUserId = snapshot.Self.UserId;
        _meetingId = snapshot.Meeting.Id;

        // Feature catch-up state.
        foreach (var f in snapshot.SharedFiles ?? []) SharedFiles.Add(f);
        if (snapshot.ActivePoll is not null)
        {
            ApplyPollStarted(snapshot.ActivePoll);
            if (snapshot.ActivePollResults is not null) ApplyPollResults(snapshot.ActivePollResults);
        }
        foreach (var s in snapshot.Whiteboard ?? []) InitialWhiteboard.Add(s);
        foreach (var w in snapshot.Waiting ?? []) Waiting.Add(w);
        HasWaiting = Waiting.Count > 0;
        if (snapshot.Breakouts is not null) ApplyBreakouts(snapshot.Breakouts);

        _selfTile = ParticipantTileViewModel.From(snapshot.Self, isSelf: true);
        Tiles.Add(_selfTile);
        foreach (var participant in snapshot.Participants)
        {
            Tiles.Add(ParticipantTileViewModel.From(participant));
        }
        RebuildChatRecipients();

        // End-to-end encryption: alone in the room we mint the meeting key;
        // otherwise ask the key holder (the host) to wrap it for our public key.
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

        foreach (var message in snapshot.RecentChat)
        {
            HandleChatReceived(message);
        }

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
        // If we never received the meeting key (e.g. the old host vanished mid-handshake),
        // ask the new host.
        if (!_e2ee.HasKey && !IsSelfHost)
        {
            _ = _hub.SendSignalAsync(newHost.ConnectionId, MeetingE2ee.SignalPublicKey, _e2ee.PublicKeyBase64);
        }
    }

    // ---- End-to-end encryption ----------------------------------------------

    private void HandleE2eeSignal(SignalMessage signal)
    {
        switch (signal.Type)
        {
            case MeetingE2ee.SignalPublicKey when _e2ee.HasKey:
                // A newcomer wants the meeting key: wrap it for their public key.
                _ = _hub.SendSignalAsync(signal.FromConnectionId, MeetingE2ee.SignalWrappedKey,
                    _e2ee.WrapKeyFor(signal.Payload));
                break;

            case MeetingE2ee.SignalWrappedKey when !_e2ee.HasKey:
                if (_e2ee.TryUnwrapKey(signal.Payload))
                {
                    IsE2eeActive = true;
                    foreach (var pending in _pendingEncrypted) HandleChatReceived(pending);
                    _pendingEncrypted.Clear();
                }
                break;
        }
    }

    private void HandleChatReceived(ChatMessageDto message)
    {
        if (_e2ee.TryDecrypt(message.Text, out var plaintext))
        {
            ChatMessages.Add(message with { Text = plaintext });
        }
        else if (MeetingE2ee.IsEncrypted(message.Text))
        {
            // Encrypted but the key hasn't arrived yet — hold and replay after unwrap.
            if (!_e2ee.HasKey) _pendingEncrypted.Add(message);
            // With a key but undecryptable: drop (tampered or foreign meeting key).
        }
        else
        {
            ChatMessages.Add(message);
        }
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

    // ---- Feature handlers --------------------------------------------------------

    private async void ShowReaction(ReactionDto r)
    {
        var item = new ReactionItem { Text = r.Emoji };
        Reactions.Add(item);
        try { await Task.Delay(4000); } catch { /* ignore */ }
        Reactions.Remove(item);
    }

    private void ApplyPollStarted(PollDto p)
    {
        HasActivePoll = true;
        PollIsClosed = p.IsClosed;
        ActivePollId = p.PollId;
        PollQuestion = p.Question;
        PollTotalVotes = 0;
        PollOptions.Clear();
        for (int i = 0; i < p.Options.Count; i++)
            PollOptions.Add(new PollOptionVm { Index = i, Text = p.Options[i] });
    }

    private void ApplyPollResults(PollResultsDto r)
    {
        if (r.PollId != ActivePollId) return;
        PollTotalVotes = r.TotalVotes;
        PollIsClosed = r.IsClosed;
        for (int i = 0; i < PollOptions.Count && i < r.Votes.Count; i++)
        {
            PollOptions[i].Votes = r.Votes[i];
            PollOptions[i].Fraction = r.TotalVotes > 0 ? (double)r.Votes[i] / r.TotalVotes : 0;
        }
    }

    private void ApplyBreakouts(BreakoutStateDto s)
    {
        HasBreakouts = s.Rooms.Count > 0;
        BreakoutsOpen = s.IsOpen;
        BreakoutRooms.Clear();
        foreach (var room in s.Rooms) BreakoutRooms.Add(room);
        var mine = s.Rooms.FirstOrDefault(r => r.Members.Any(m => m.UserId == _selfUserId));
        MyBreakoutRoom = mine is null ? "" : s.IsOpen ? mine.Name : $"{mine.Name} (opens soon)";
    }

    // ---- Feature commands --------------------------------------------------------

    [RelayCommand]
    private async Task SendReactionAsync(string? emoji)
    {
        if (string.IsNullOrEmpty(emoji)) return;
        try { await _hub.SendReactionAsync(emoji); } catch { /* best effort */ }
    }

    [RelayCommand]
    private async Task AdmitAsync(WaitingParticipantDto? w)
    {
        if (w is null) return;
        try { await _hub.AdmitParticipantAsync(w.ConnectionId); } catch { StatusMessage = "Admit failed."; }
    }

    [RelayCommand]
    private async Task DenyAsync(WaitingParticipantDto? w)
    {
        if (w is null) return;
        try { await _hub.DenyParticipantAsync(w.ConnectionId); } catch { /* best effort */ }
    }

    [RelayCommand]
    private async Task CreatePollAsync()
    {
        var q = NewPollQuestion.Trim();
        var opts = NewPollOptions
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (q.Length == 0 || opts.Count < 2)
        {
            StatusMessage = "Enter a question and at least two options (one per line).";
            return;
        }
        try
        {
            await _hub.CreatePollAsync(q, opts);
            NewPollQuestion = "";
            NewPollOptions = "";
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    [RelayCommand]
    private async Task VoteAsync(PollOptionVm? option)
    {
        if (option is null || PollIsClosed) return;
        foreach (var o in PollOptions) o.IsMyVote = o.Index == option.Index;
        try { await _hub.VotePollAsync(ActivePollId, option.Index); } catch { /* best effort */ }
    }

    [RelayCommand]
    private async Task ClosePollAsync()
    {
        if (!HasActivePoll) return;
        try { await _hub.ClosePollAsync(ActivePollId); } catch { /* best effort */ }
    }

    [RelayCommand]
    private async Task ShareFileAsync()
    {
        if (PickFileToUpload is null) return;
        var path = await PickFileToUpload();
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            StatusMessage = "Uploading…";
            var uploaded = await _api.UploadFileAsync(_meetingId, path);
            await _hub.ShareFileAsync(uploaded.FileId);
            StatusMessage = $"Shared {uploaded.FileName}.";
        }
        catch (Exception ex) { StatusMessage = $"Upload failed: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task DownloadFileAsync(MeetingFileDto? file)
    {
        if (file is null || PickSaveLocation is null) return;
        var dest = await PickSaveLocation(file.FileName);
        if (string.IsNullOrEmpty(dest)) return;
        try
        {
            await _api.DownloadFileAsync(file.DownloadPath, dest);
            StatusMessage = $"Saved {file.FileName}.";
        }
        catch (Exception ex) { StatusMessage = $"Download failed: {ex.Message}"; }
    }

    /// <summary>Called by the whiteboard view when the user draws a stroke.</summary>
    public Task SendStrokeAsync(WhiteboardStrokeDto stroke) => _hub.WhiteboardDrawAsync(stroke);

    [RelayCommand]
    private async Task ClearWhiteboardAsync()
    {
        try { await _hub.WhiteboardClearAsync(); } catch { /* best effort */ }
    }

    [RelayCommand]
    private async Task CreateBreakoutsAsync()
    {
        if (!int.TryParse(BreakoutRoomCount, out var n) || n < 1)
        {
            StatusMessage = "Enter the number of rooms.";
            return;
        }
        try { await _hub.CreateBreakoutRoomsAsync(n); } catch (Exception ex) { StatusMessage = ex.Message; }
    }

    [RelayCommand]
    private async Task AutoAssignBreakoutsAsync()
    {
        if (!HasBreakouts || BreakoutRooms.Count == 0) return;
        int i = 0;
        foreach (var tile in Tiles.Where(t => !t.IsSelf))
        {
            try { await _hub.AssignBreakoutAsync(tile.ConnectionId, i % BreakoutRooms.Count); } catch { /* skip */ }
            i++;
        }
        StatusMessage = "Assigned participants to rooms.";
    }

    [RelayCommand]
    private async Task OpenBreakoutsAsync()
    {
        try { await _hub.OpenBreakoutsAsync(); } catch { /* best effort */ }
    }

    [RelayCommand]
    private async Task CloseBreakoutsAsync()
    {
        try { await _hub.CloseBreakoutsAsync(); } catch { /* best effort */ }
    }

    // ---- Teardown --------------------------------------------------------------

    public async Task ShutdownAsync()
    {
        await _webRtc.DisposeAsync();
        await _hub.DisposeAsync();
        _e2ee.Dispose();
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
