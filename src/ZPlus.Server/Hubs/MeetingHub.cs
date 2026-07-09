using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using ZPlus.Server.Controllers;
using ZPlus.Server.Data;
using ZPlus.Server.Models;
using ZPlus.Server.Services;
using ZPlus.Shared.Dtos;

namespace ZPlus.Server.Hubs;

[Authorize]
public class MeetingHub(
    AppDbContext db,
    MeetingStateStore state,
    SettingsService settings,
    PasswordService passwords,
    ILogger<MeetingHub> logger) : Hub
{
    private Guid CurrentUserId => Guid.Parse(Context.User!.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private string CurrentDisplayName => Context.User!.FindFirstValue(ClaimTypes.Name) ?? "Unknown";

    public static string GroupName(Guid meetingId) => $"meeting:{meetingId}";

    // ---- Lifecycle -------------------------------------------------------

    public async Task<MeetingJoinedSnapshot> JoinMeeting(string meetingCode, string? password)
    {
        var code = NormalizeCode(meetingCode);
        var meeting = await db.Meetings.Include(m => m.Host)
            .SingleOrDefaultAsync(m => m.MeetingCode == code)
            ?? throw new HubException("No meeting found with that ID.");

        if (meeting.EndedAtUtc is not null)
            throw new HubException("That meeting has already ended.");

        var account = await db.Users.FindAsync(CurrentUserId);
        if (account is null || account.IsDisabled)
            throw new HubException("This account has been disabled. Contact your administrator.");

        bool isHost = meeting.HostUserId == CurrentUserId;
        if (!isHost && meeting.PasswordHash is not null)
        {
            if (string.IsNullOrEmpty(password) || !passwords.Verify(meeting.PasswordHash, password))
                throw new HubException("Incorrect meeting password.");
        }

        if (!meeting.IsActive)
        {
            if (!isHost) throw new HubException("The host has not started this meeting yet.");
            meeting.IsActive = true;
            await db.SaveChangesAsync();
        }

        var participant = new LiveParticipant
        {
            UserId = CurrentUserId,
            ConnectionId = Context.ConnectionId,
            DisplayName = CurrentDisplayName,
            IsHost = isHost,
        };

        var live = state.GetOrCreate(meeting.Id);

        // Waiting room: a non-host joiner is parked until the host admits them.
        if (meeting.WaitingRoomEnabled && !isHost)
        {
            state.AddWaiting(meeting.Id, participant);
            var waitingDto = new WaitingParticipantDto(participant.ConnectionId, participant.UserId, participant.DisplayName);
            var hostConnections = live.Participants.Values.Where(p => p.IsHost).Select(p => p.ConnectionId).ToList();
            if (hostConnections.Count > 0)
                await Clients.Clients(hostConnections).SendAsync(HubEvents.ParticipantWaiting, waitingDto);
            logger.LogInformation("{User} is waiting to join meeting {Code}", CurrentDisplayName, code);
            return new MeetingJoinedSnapshot(
                MeetingsController.ToDto(meeting, meeting.Host?.DisplayName ?? ""),
                participant.ToDto(), [], [], InWaitingRoom: true);
        }

        var maxParticipants = (await settings.GetAsync()).MaxParticipantsPerMeeting;
        if (live.Participants.Count >= maxParticipants)
            throw new HubException($"This meeting is full (limit {maxParticipants} participants).");

        logger.LogInformation("{User} joined meeting {Code}", CurrentDisplayName, code);
        return await AdmitAsync(meeting, participant);
    }

    /// <summary>Adds a participant to the live meeting + group, records attendance, and returns their snapshot.</summary>
    private async Task<MeetingJoinedSnapshot> AdmitAsync(Meeting meeting, LiveParticipant participant)
    {
        var live = state.GetOrCreate(meeting.Id);
        var existing = live.Participants.Values.Select(p => p.ToDto()).ToList();
        state.AddParticipant(meeting.Id, participant);

        db.MeetingParticipants.Add(new MeetingParticipantRecord
        {
            MeetingId = meeting.Id,
            UserId = participant.UserId,
            JoinedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        await Groups.AddToGroupAsync(participant.ConnectionId, GroupName(meeting.Id));
        await Clients.GroupExcept(GroupName(meeting.Id), participant.ConnectionId)
            .SendAsync(HubEvents.ParticipantJoined, participant.ToDto());

        return await BuildSnapshotAsync(meeting, participant, existing);
    }

    /// <summary>Gathers all catch-up state (chat, files, active poll, whiteboard, waiting list, breakouts).</summary>
    private async Task<MeetingJoinedSnapshot> BuildSnapshotAsync(Meeting meeting, LiveParticipant participant, List<ParticipantDto> existing)
    {
        var live = state.GetOrCreate(meeting.Id);

        var recentChat = await db.ChatMessages
            .Where(c => c.MeetingId == meeting.Id && !c.IsPrivate)
            .OrderByDescending(c => c.Id).Take(100).OrderBy(c => c.Id)
            .Select(c => new ChatMessageDto(c.SenderUserId, c.SenderDisplayName, c.Text, c.SentAtUtc, c.IsPrivate, c.RecipientUserId))
            .ToListAsync();

        var files = await db.MeetingFiles
            .Where(f => f.MeetingId == meeting.Id)
            .OrderBy(f => f.SharedAtUtc)
            .Select(f => new MeetingFileDto(f.Id, f.FileName, f.Size, f.ContentType,
                f.SenderUserId, f.SenderDisplayName, f.SharedAtUtc, "/api/files/" + f.Id))
            .ToListAsync();

        var poll = await db.Polls.Where(p => p.MeetingId == meeting.Id && !p.IsClosed)
            .OrderByDescending(p => p.CreatedAtUtc).FirstOrDefaultAsync();
        PollDto? pollDto = poll is null ? null : ToPollDto(poll);
        PollResultsDto? pollResults = poll is null ? null : await ComputeResultsAsync(poll);

        List<WhiteboardStrokeDto> strokes;
        lock (live.WhiteboardLock) strokes = live.WhiteboardStrokes.ToList();

        List<WaitingParticipantDto>? waiting = participant.IsHost
            ? live.Waiting.Values.Select(w => new WaitingParticipantDto(w.ConnectionId, w.UserId, w.DisplayName)).ToList()
            : null;

        return new MeetingJoinedSnapshot(
            MeetingsController.ToDto(meeting, meeting.Host?.DisplayName ?? ""),
            participant.ToDto(), existing, recentChat,
            InWaitingRoom: false,
            SharedFiles: files,
            ActivePoll: pollDto,
            ActivePollResults: pollResults,
            Whiteboard: strokes,
            Waiting: waiting,
            Breakouts: BuildBreakoutDto(live));
    }

    public Task LeaveMeeting() => HandleDeparture(Context.ConnectionId);

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await HandleDeparture(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    private async Task HandleDeparture(string connectionId)
    {
        var removed = state.RemoveParticipant(connectionId);
        if (removed is null)
        {
            // They may have been sitting in the waiting room rather than admitted.
            var waiting = state.RemoveWaiting(connectionId);
            if (waiting is not null)
            {
                var (waitMeeting, _) = waiting.Value;
                await NotifyWaitingClearedAsync(waitMeeting, connectionId);
            }
            return;
        }
        var (meeting, participant) = removed.Value;

        // Drop any breakout assignment they held.
        meeting.Breakouts?.Assignments.TryRemove(participant.UserId, out _);

        await Groups.RemoveFromGroupAsync(connectionId, GroupName(meeting.MeetingId));
        await Clients.Group(GroupName(meeting.MeetingId))
            .SendAsync(HubEvents.ParticipantLeft, participant.ToDto());

        var record = await db.MeetingParticipants
            .Where(p => p.MeetingId == meeting.MeetingId && p.UserId == participant.UserId && p.LeftAtUtc == null)
            .OrderByDescending(p => p.Id)
            .FirstOrDefaultAsync();
        if (record is not null)
        {
            record.LeftAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        // If the host left and others remain, promote the longest-connected participant.
        if (participant.IsHost)
        {
            var successor = meeting.Participants.Values.FirstOrDefault();
            if (successor is not null && !meeting.Participants.Values.Any(p => p.IsHost))
            {
                successor.IsHost = true;
                await Clients.Group(GroupName(meeting.MeetingId))
                    .SendAsync(HubEvents.HostChanged, successor.ToDto());
            }
        }
    }

    // ---- Chat ------------------------------------------------------------

    public async Task SendChat(string text, Guid? recipientUserId)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        text = text.Trim();
        // Truncating would corrupt end-to-end encrypted payloads, so reject instead.
        if (text.Length > 8000) throw new HubException("Message too long.");

        var found = state.Find(Context.ConnectionId) ?? throw new HubException("You are not in a meeting.");
        var (meeting, sender) = found;

        var message = new ChatMessageDto(
            sender.UserId, sender.DisplayName, text, DateTime.UtcNow,
            recipientUserId is not null, recipientUserId);

        db.ChatMessages.Add(new ChatMessageRecord
        {
            MeetingId = meeting.MeetingId,
            SenderUserId = sender.UserId,
            SenderDisplayName = sender.DisplayName,
            Text = text,
            IsPrivate = message.IsPrivate,
            RecipientUserId = recipientUserId,
            SentAtUtc = message.SentAtUtc,
        });
        await db.SaveChangesAsync();

        if (recipientUserId is null)
        {
            // When breakout rooms are open, public chat is scoped to the sender's room (+ hosts).
            if (meeting.Breakouts is { IsOpen: true } b && b.Assignments.TryGetValue(sender.UserId, out var room))
            {
                var targets = meeting.Participants.Values
                    .Where(p => p.IsHost || (b.Assignments.TryGetValue(p.UserId, out var r) && r == room))
                    .Select(p => p.ConnectionId).Distinct().ToList();
                await Clients.Clients(targets).SendAsync(HubEvents.ChatReceived, message);
            }
            else
            {
                await Clients.Group(GroupName(meeting.MeetingId)).SendAsync(HubEvents.ChatReceived, message);
            }
        }
        else
        {
            var recipients = meeting.Participants.Values
                .Where(p => p.UserId == recipientUserId.Value)
                .Select(p => p.ConnectionId)
                .Append(Context.ConnectionId)
                .Distinct()
                .ToList();
            await Clients.Clients(recipients).SendAsync(HubEvents.ChatReceived, message);
        }
    }

    // ---- WebRTC signaling relay -------------------------------------------

    public async Task SendSignal(string targetConnectionId, string type, string payload)
    {
        var found = state.Find(Context.ConnectionId) ?? throw new HubException("You are not in a meeting.");
        // Only relay between participants of the same meeting.
        if (!found.Meeting.Participants.ContainsKey(targetConnectionId)) return;

        await Clients.Client(targetConnectionId)
            .SendAsync(HubEvents.SignalReceived, new SignalMessage(Context.ConnectionId, type, payload));
    }

    // ---- Self state --------------------------------------------------------

    public Task SetMuted(bool muted) => UpdateSelf(p => p.IsMuted = muted);

    public Task SetVideoOn(bool videoOn) => UpdateSelf(p => p.IsVideoOn = videoOn);

    private async Task UpdateSelf(Action<LiveParticipant> update)
    {
        var found = state.Find(Context.ConnectionId);
        if (found is null) return;
        update(found.Value.Participant);
        await Clients.Group(GroupName(found.Value.Meeting.MeetingId))
            .SendAsync(HubEvents.ParticipantStateChanged, found.Value.Participant.ToDto());
    }

    // ---- Host controls ------------------------------------------------------

    public async Task MuteAll()
    {
        var (meeting, _) = RequireHost();
        foreach (var p in meeting.Participants.Values.Where(p => !p.IsHost && !p.IsMuted))
        {
            p.IsMuted = true;
            await Clients.Client(p.ConnectionId).SendAsync(HubEvents.ForcedMute);
            await Clients.Group(GroupName(meeting.MeetingId))
                .SendAsync(HubEvents.ParticipantStateChanged, p.ToDto());
        }
    }

    public async Task AskToUnmute(string targetConnectionId)
    {
        var (meeting, _) = RequireHost();
        if (meeting.Participants.ContainsKey(targetConnectionId))
            await Clients.Client(targetConnectionId).SendAsync(HubEvents.UnmuteRequested);
    }

    public async Task RemoveParticipant(string targetConnectionId)
    {
        var (meeting, _) = RequireHost();
        if (!meeting.Participants.TryGetValue(targetConnectionId, out var target) || target.IsHost) return;

        await Clients.Client(targetConnectionId).SendAsync(HubEvents.RemovedFromMeeting);
        await HandleDeparture(targetConnectionId);
    }

    public async Task TransferHost(string targetConnectionId)
    {
        var (meeting, host) = RequireHost();
        if (!meeting.Participants.TryGetValue(targetConnectionId, out var target)) return;

        host.IsHost = false;
        target.IsHost = true;
        await Clients.Group(GroupName(meeting.MeetingId)).SendAsync(HubEvents.HostChanged, target.ToDto());
        await Clients.Group(GroupName(meeting.MeetingId)).SendAsync(HubEvents.ParticipantStateChanged, host.ToDto());
    }

    public async Task EndMeetingForAll()
    {
        var (live, _) = RequireHost();

        var meeting = await db.Meetings.FindAsync(live.MeetingId);
        if (meeting is not null)
        {
            meeting.IsActive = false;
            meeting.EndedAtUtc = DateTime.UtcNow;
            var openRecords = await db.MeetingParticipants
                .Where(p => p.MeetingId == live.MeetingId && p.LeftAtUtc == null)
                .ToListAsync();
            foreach (var r in openRecords) r.LeftAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        await Clients.Group(GroupName(live.MeetingId)).SendAsync(HubEvents.MeetingEnded);
        foreach (var connectionId in live.Participants.Keys)
        {
            await Groups.RemoveFromGroupAsync(connectionId, GroupName(live.MeetingId));
        }
        state.EndMeeting(live.MeetingId);
    }

    // ---- Reactions ---------------------------------------------------------

    public async Task SendReaction(string emoji)
    {
        if (string.IsNullOrWhiteSpace(emoji) || emoji.Length > 16) return;
        var found = state.Find(Context.ConnectionId);
        if (found is null) return;
        var (meeting, sender) = found.Value;
        await Clients.Group(GroupName(meeting.MeetingId))
            .SendAsync(HubEvents.ReactionReceived, new ReactionDto(sender.UserId, sender.DisplayName, emoji));
    }

    // ---- Waiting room ------------------------------------------------------

    public async Task AdmitParticipant(string targetConnectionId)
    {
        var (live, _) = RequireHost();
        if (!live.Waiting.ContainsKey(targetConnectionId)) return;

        var meeting = await db.Meetings.Include(m => m.Host).FirstOrDefaultAsync(m => m.Id == live.MeetingId);
        if (meeting is null) return;

        var maxParticipants = (await settings.GetAsync()).MaxParticipantsPerMeeting;
        if (live.Participants.Count >= maxParticipants)
        {
            await Clients.Client(targetConnectionId).SendAsync(HubEvents.WaitingDenied, "The meeting is full.");
            return;
        }

        var removed = state.RemoveWaiting(targetConnectionId);
        if (removed is null) return;
        var waiter = removed.Value.Participant;

        var snapshot = await AdmitAsync(meeting, waiter);
        await Clients.Client(targetConnectionId).SendAsync(HubEvents.AdmittedToMeeting, snapshot);
        await NotifyWaitingClearedAsync(live, targetConnectionId);
    }

    public async Task DenyParticipant(string targetConnectionId)
    {
        var (live, _) = RequireHost();
        if (state.RemoveWaiting(targetConnectionId) is null) return;
        await Clients.Client(targetConnectionId)
            .SendAsync(HubEvents.WaitingDenied, "The host declined your request to join.");
        await NotifyWaitingClearedAsync(live, targetConnectionId);
    }

    private async Task NotifyWaitingClearedAsync(LiveMeeting live, string connectionId)
    {
        var hostConnections = live.Participants.Values.Where(p => p.IsHost).Select(p => p.ConnectionId).ToList();
        if (hostConnections.Count > 0)
            await Clients.Clients(hostConnections).SendAsync(HubEvents.WaitingCleared, connectionId);
    }

    // ---- Polls -------------------------------------------------------------

    public async Task CreatePoll(string question, List<string> options)
    {
        var (live, _) = RequireHost();
        question = (question ?? "").Trim();
        var opts = (options ?? []).Select(o => (o ?? "").Trim()).Where(o => o.Length > 0).ToList();
        if (question.Length == 0 || opts.Count < 2)
            throw new HubException("A poll needs a question and at least two options.");
        if (opts.Count > 10) opts = opts.Take(10).ToList();

        var open = await db.Polls.Where(p => p.MeetingId == live.MeetingId && !p.IsClosed).ToListAsync();
        foreach (var o in open) o.IsClosed = true;

        var poll = new Poll
        {
            MeetingId = live.MeetingId,
            Question = question,
            Options = string.Join('\n', opts),
            CreatedByUserId = CurrentUserId,
        };
        db.Polls.Add(poll);
        await db.SaveChangesAsync();

        await Clients.Group(GroupName(live.MeetingId)).SendAsync(HubEvents.PollStarted, ToPollDto(poll));
        await Clients.Group(GroupName(live.MeetingId)).SendAsync(HubEvents.PollUpdated, await ComputeResultsAsync(poll));
    }

    public async Task VotePoll(Guid pollId, int optionIndex)
    {
        var found = state.Find(Context.ConnectionId) ?? throw new HubException("You are not in a meeting.");
        var (meeting, _) = found;
        var poll = await db.Polls.FirstOrDefaultAsync(p => p.Id == pollId && p.MeetingId == meeting.MeetingId);
        if (poll is null || poll.IsClosed) return;
        if (optionIndex < 0 || optionIndex >= ToPollDto(poll).Options.Count) return;

        var existing = await db.PollVotes.FirstOrDefaultAsync(v => v.PollId == pollId && v.UserId == CurrentUserId);
        if (existing is null)
            db.PollVotes.Add(new PollVote { PollId = pollId, UserId = CurrentUserId, OptionIndex = optionIndex });
        else
            existing.OptionIndex = optionIndex;
        await db.SaveChangesAsync();

        await Clients.Group(GroupName(meeting.MeetingId)).SendAsync(HubEvents.PollUpdated, await ComputeResultsAsync(poll));
    }

    public async Task ClosePoll(Guid pollId)
    {
        var (live, _) = RequireHost();
        var poll = await db.Polls.FirstOrDefaultAsync(p => p.Id == pollId && p.MeetingId == live.MeetingId);
        if (poll is null || poll.IsClosed) return;
        poll.IsClosed = true;
        await db.SaveChangesAsync();
        await Clients.Group(GroupName(live.MeetingId)).SendAsync(HubEvents.PollUpdated, await ComputeResultsAsync(poll));
        await Clients.Group(GroupName(live.MeetingId)).SendAsync(HubEvents.PollClosed, pollId);
    }

    private static PollDto ToPollDto(Poll p) => new(
        p.Id, p.Question,
        p.Options.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
        p.IsClosed);

    private async Task<PollResultsDto> ComputeResultsAsync(Poll p)
    {
        int optionCount = ToPollDto(p).Options.Count;
        var votes = await db.PollVotes.Where(v => v.PollId == p.Id).ToListAsync();
        var counts = new int[optionCount];
        foreach (var v in votes)
            if (v.OptionIndex >= 0 && v.OptionIndex < optionCount) counts[v.OptionIndex]++;
        return new PollResultsDto(p.Id, counts.ToList(), votes.Count, p.IsClosed);
    }

    // ---- File sharing ------------------------------------------------------

    public async Task ShareFile(Guid fileId)
    {
        var found = state.Find(Context.ConnectionId) ?? throw new HubException("You are not in a meeting.");
        var (meeting, sender) = found;
        var file = await db.MeetingFiles
            .FirstOrDefaultAsync(f => f.Id == fileId && f.MeetingId == meeting.MeetingId && f.SenderUserId == sender.UserId);
        if (file is null) return;

        var dto = new MeetingFileDto(file.Id, file.FileName, file.Size, file.ContentType,
            file.SenderUserId, file.SenderDisplayName, file.SharedAtUtc, "/api/files/" + file.Id);
        await Clients.Group(GroupName(meeting.MeetingId)).SendAsync(HubEvents.FileShared, dto);
    }

    // ---- Whiteboard --------------------------------------------------------

    public async Task WhiteboardDraw(WhiteboardStrokeDto stroke)
    {
        if (stroke?.Points is null || stroke.Points.Count < 2) return;
        var found = state.Find(Context.ConnectionId);
        if (found is null) return;
        var (meeting, _) = found.Value;
        lock (meeting.WhiteboardLock)
        {
            if (meeting.WhiteboardStrokes.Count < 5000) meeting.WhiteboardStrokes.Add(stroke);
        }
        await Clients.OthersInGroup(GroupName(meeting.MeetingId))
            .SendAsync(HubEvents.WhiteboardStrokeReceived, stroke);
    }

    public async Task WhiteboardClear()
    {
        var found = state.Find(Context.ConnectionId);
        if (found is null) return;
        var (meeting, _) = found.Value;
        lock (meeting.WhiteboardLock) meeting.WhiteboardStrokes.Clear();
        await Clients.Group(GroupName(meeting.MeetingId)).SendAsync(HubEvents.WhiteboardCleared);
    }

    // ---- Breakout rooms ----------------------------------------------------

    public async Task CreateBreakoutRooms(int count)
    {
        var (live, _) = RequireHost();
        count = Math.Clamp(count, 1, 20);
        var breakouts = new BreakoutState();
        for (int i = 0; i < count; i++) breakouts.RoomNames.Add($"Room {i + 1}");
        live.Breakouts = breakouts;
        await Clients.Group(GroupName(live.MeetingId)).SendAsync(HubEvents.BreakoutsUpdated, BuildBreakoutDto(live));
    }

    public async Task AssignBreakout(string targetConnectionId, int roomIndex)
    {
        var (live, _) = RequireHost();
        if (live.Breakouts is null) return;
        if (!live.Participants.TryGetValue(targetConnectionId, out var target)) return;

        if (roomIndex < 0 || roomIndex >= live.Breakouts.RoomNames.Count)
            live.Breakouts.Assignments.TryRemove(target.UserId, out _);
        else
            live.Breakouts.Assignments[target.UserId] = roomIndex;

        await Clients.Group(GroupName(live.MeetingId)).SendAsync(HubEvents.BreakoutsUpdated, BuildBreakoutDto(live));
        if (live.Breakouts.IsOpen)
            await SendBreakoutAssignmentAsync(live, target);
    }

    public async Task OpenBreakouts()
    {
        var (live, _) = RequireHost();
        if (live.Breakouts is null) return;
        live.Breakouts.IsOpen = true;
        foreach (var p in live.Participants.Values)
            await SendBreakoutAssignmentAsync(live, p);
        await Clients.Group(GroupName(live.MeetingId)).SendAsync(HubEvents.BreakoutsOpened);
        await Clients.Group(GroupName(live.MeetingId)).SendAsync(HubEvents.BreakoutsUpdated, BuildBreakoutDto(live));
    }

    public async Task CloseBreakouts()
    {
        var (live, _) = RequireHost();
        if (live.Breakouts is null) return;
        live.Breakouts.IsOpen = false;
        await Clients.Group(GroupName(live.MeetingId)).SendAsync(HubEvents.BreakoutsClosed);
        await Clients.Group(GroupName(live.MeetingId)).SendAsync(HubEvents.BreakoutsUpdated, BuildBreakoutDto(live));
    }

    private async Task SendBreakoutAssignmentAsync(LiveMeeting live, LiveParticipant p)
    {
        var b = live.Breakouts;
        if (b is null) return;
        int room = b.Assignments.TryGetValue(p.UserId, out var ri) ? ri : -1;
        string name = room >= 0 && room < b.RoomNames.Count ? b.RoomNames[room] : "";
        await Clients.Client(p.ConnectionId).SendAsync(HubEvents.BreakoutAssigned, room, name);
    }

    private static BreakoutStateDto? BuildBreakoutDto(LiveMeeting live)
    {
        var b = live.Breakouts;
        if (b is null) return null;
        var rooms = new List<BreakoutRoomDto>();
        for (int i = 0; i < b.RoomNames.Count; i++)
        {
            var members = b.Assignments.Where(kv => kv.Value == i)
                .Select(kv =>
                {
                    var p = live.Participants.Values.FirstOrDefault(x => x.UserId == kv.Key);
                    return new WaitingParticipantDto(p?.ConnectionId ?? "", kv.Key, p?.DisplayName ?? "");
                })
                .Where(m => m.ConnectionId.Length > 0)
                .ToList();
            rooms.Add(new BreakoutRoomDto(i, b.RoomNames[i], members));
        }
        return new BreakoutStateDto(rooms, b.IsOpen);
    }

    private (LiveMeeting Meeting, LiveParticipant Participant) RequireHost()
    {
        var found = state.Find(Context.ConnectionId) ?? throw new HubException("You are not in a meeting.");
        if (!found.Participant.IsHost) throw new HubException("Only the host can do that.");
        return found;
    }

    private static string NormalizeCode(string raw)
    {
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        return digits.Length == 9 ? $"{digits[..3]}-{digits[3..6]}-{digits[6..]}" : raw.Trim();
    }
}
