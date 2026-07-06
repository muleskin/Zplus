using System.Collections.Concurrent;
using ZPlus.Shared.Dtos;

namespace ZPlus.Server.Services;

/// <summary>Live (in-memory) state for one participant in an active meeting.</summary>
public class LiveParticipant
{
    public required Guid UserId { get; init; }
    public required string ConnectionId { get; init; }
    public required string DisplayName { get; init; }
    public bool IsHost { get; set; }
    public bool IsMuted { get; set; }
    public bool IsVideoOn { get; set; }

    public ParticipantDto ToDto() => new(UserId, ConnectionId, DisplayName, IsHost, IsMuted, IsVideoOn);
}

/// <summary>Live state for one active meeting.</summary>
public class LiveMeeting
{
    public required Guid MeetingId { get; init; }
    public readonly ConcurrentDictionary<string, LiveParticipant> Participants = new();
}

/// <summary>
/// Tracks who is currently connected to which meeting. This state is transient by design:
/// the durable record (meetings, attendance, chat history) lives in the database.
/// </summary>
public class MeetingStateStore
{
    private readonly ConcurrentDictionary<Guid, LiveMeeting> _meetings = new();
    private readonly ConcurrentDictionary<string, Guid> _connectionToMeeting = new();

    public LiveMeeting GetOrCreate(Guid meetingId) =>
        _meetings.GetOrAdd(meetingId, id => new LiveMeeting { MeetingId = id });

    public LiveMeeting? Get(Guid meetingId) =>
        _meetings.TryGetValue(meetingId, out var m) ? m : null;

    public void AddParticipant(Guid meetingId, LiveParticipant participant)
    {
        GetOrCreate(meetingId).Participants[participant.ConnectionId] = participant;
        _connectionToMeeting[participant.ConnectionId] = meetingId;
    }

    /// <summary>Removes the connection and returns the meeting and participant it belonged to, if any.</summary>
    public (LiveMeeting Meeting, LiveParticipant Participant)? RemoveParticipant(string connectionId)
    {
        if (!_connectionToMeeting.TryRemove(connectionId, out var meetingId)) return null;
        if (!_meetings.TryGetValue(meetingId, out var meeting)) return null;
        if (!meeting.Participants.TryRemove(connectionId, out var participant)) return null;
        if (meeting.Participants.IsEmpty) _meetings.TryRemove(meetingId, out _);
        return (meeting, participant);
    }

    public (LiveMeeting Meeting, LiveParticipant Participant)? Find(string connectionId)
    {
        if (!_connectionToMeeting.TryGetValue(connectionId, out var meetingId)) return null;
        if (!_meetings.TryGetValue(meetingId, out var meeting)) return null;
        if (!meeting.Participants.TryGetValue(connectionId, out var participant)) return null;
        return (meeting, participant);
    }

    public void EndMeeting(Guid meetingId)
    {
        if (_meetings.TryRemove(meetingId, out var meeting))
        {
            foreach (var connectionId in meeting.Participants.Keys)
            {
                _connectionToMeeting.TryRemove(connectionId, out _);
            }
        }
    }
}
