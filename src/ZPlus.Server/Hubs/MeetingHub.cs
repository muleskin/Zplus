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
        }

        var participant = new LiveParticipant
        {
            UserId = CurrentUserId,
            ConnectionId = Context.ConnectionId,
            DisplayName = CurrentDisplayName,
            IsHost = isHost,
        };

        var live = state.GetOrCreate(meeting.Id);
        var maxParticipants = (await settings.GetAsync()).MaxParticipantsPerMeeting;
        if (live.Participants.Count >= maxParticipants)
            throw new HubException($"This meeting is full (limit {maxParticipants} participants).");

        var existing = live.Participants.Values.Select(p => p.ToDto()).ToList();
        state.AddParticipant(meeting.Id, participant);

        db.MeetingParticipants.Add(new MeetingParticipantRecord
        {
            MeetingId = meeting.Id,
            UserId = CurrentUserId,
            JoinedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(meeting.Id));
        await Clients.OthersInGroup(GroupName(meeting.Id))
            .SendAsync(HubEvents.ParticipantJoined, participant.ToDto());

        var recentChat = await db.ChatMessages
            .Where(c => c.MeetingId == meeting.Id && !c.IsPrivate)
            .OrderByDescending(c => c.Id)
            .Take(100)
            .OrderBy(c => c.Id)
            .Select(c => new ChatMessageDto(c.SenderUserId, c.SenderDisplayName, c.Text, c.SentAtUtc, c.IsPrivate, c.RecipientUserId))
            .ToListAsync();

        logger.LogInformation("{User} joined meeting {Code}", CurrentDisplayName, code);

        return new MeetingJoinedSnapshot(
            MeetingsController.ToDto(meeting, meeting.Host?.DisplayName ?? ""),
            participant.ToDto(),
            existing,
            recentChat);
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
        if (removed is null) return;
        var (meeting, participant) = removed.Value;

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
        if (text.Length > 4000) text = text[..4000];

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
            await Clients.Group(GroupName(meeting.MeetingId)).SendAsync(HubEvents.ChatReceived, message);
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
