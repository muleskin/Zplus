using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZPlus.Client.Services;
using ZPlus.Shared.Dtos;

namespace ZPlus.Client.ViewModels;

public partial class HomeViewModel : ObservableObject
{
    private readonly ApiClient _api = new();

    [ObservableProperty] private string _joinCode = "";
    [ObservableProperty] private string _joinPassword = "";
    [ObservableProperty] private string _scheduleTopic = "";
    [ObservableProperty] private string _schedulePassword = "";
    [ObservableProperty] private string _scheduleInvites = "";
    [ObservableProperty] private DateTime _scheduleDate = DateTime.Today.AddDays(1);
    [ObservableProperty] private string _scheduleTime = "09:00";
    [ObservableProperty] private string _scheduleDuration = "60";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string? _info;

    public ObservableCollection<MeetingDto> MyMeetings { get; } = [];

    public string WelcomeText => $"Welcome, {AppSession.Current.User?.DisplayName}";

    /// <summary>Raised when a meeting should be opened; args are the meeting code and password used to enter.</summary>
    public event Action<string, string?>? OpenMeetingRequested;

    [RelayCommand]
    private async Task StartInstantMeetingAsync()
    {
        await RunAsync(async () =>
        {
            var topic = $"{AppSession.Current.User?.DisplayName}'s Meeting";
            var created = await _api.CreateMeetingAsync(new CreateMeetingRequest(topic, null, null, null));
            OpenMeetingRequested?.Invoke(created.Meeting.MeetingCode, null);
        });
    }

    [RelayCommand]
    private async Task JoinMeetingAsync()
    {
        if (string.IsNullOrWhiteSpace(JoinCode))
        {
            Error = "Enter a meeting ID to join.";
            return;
        }
        await RunAsync(async () =>
        {
            var password = string.IsNullOrEmpty(JoinPassword) ? null : JoinPassword;
            // Validate code/password up front so the user gets a clear error here.
            await _api.LookupMeetingAsync(new JoinLookupRequest(JoinCode, password));
            OpenMeetingRequested?.Invoke(JoinCode, password);
        });
    }

    /// <summary>
    /// If the app was launched (or reactivated) via a zplus:// invitation link, fill in the
    /// Join fields and go straight into the meeting. Call after the Home screen is shown.
    /// </summary>
    public async Task TryPendingJoinAsync()
    {
        var pending = Services.DeepLink.TakePendingJoin();
        if (pending is null) return;
        JoinCode = pending.Code;
        JoinPassword = pending.Password ?? "";
        await JoinMeetingAsync();
    }

    [RelayCommand]
    private async Task ScheduleMeetingAsync()
    {
        if (string.IsNullOrWhiteSpace(ScheduleTopic))
        {
            Error = "Enter a topic for the scheduled meeting.";
            return;
        }
        if (!TimeSpan.TryParse(ScheduleTime, out var time))
        {
            Error = "Enter the start time as HH:mm, e.g. 14:30.";
            return;
        }
        if (!int.TryParse(ScheduleDuration, out var duration) || duration <= 0)
        {
            Error = "Enter the duration in minutes.";
            return;
        }
        var invites = ScheduleInvites
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        await RunAsync(async () =>
        {
            var startUtc = (ScheduleDate.Date + time).ToUniversalTime();
            var password = string.IsNullOrEmpty(SchedulePassword) ? null : SchedulePassword;
            var created = await _api.CreateMeetingAsync(
                new CreateMeetingRequest(ScheduleTopic.Trim(), password, startUtc, duration,
                    invites.Count > 0 ? invites : null));

            Info = $"Scheduled \"{created.Meeting.Topic}\" — meeting ID {created.Meeting.MeetingCode}";
            if (created.InvitesSent > 0)
                Info += $" · {created.InvitesSent} invitation(s) emailed";
            if (created.InviteFailures.Count > 0)
                Error = $"Some invitations failed: {string.Join("; ", created.InviteFailures.Take(3))}";

            ScheduleTopic = "";
            SchedulePassword = "";
            ScheduleInvites = "";
            await RefreshMeetingsAsync();
        });
    }

    [RelayCommand]
    public async Task RefreshMeetingsAsync()
    {
        try
        {
            var meetings = await _api.GetMyMeetingsAsync();
            MyMeetings.Clear();
            foreach (var m in meetings) MyMeetings.Add(m);
        }
        catch
        {
            // Non-fatal; the list simply stays stale.
        }
    }

    [RelayCommand]
    private void StartFromList(MeetingDto? meeting)
    {
        if (meeting is null) return;
        OpenMeetingRequested?.Invoke(meeting.MeetingCode, null);
    }

    private async Task RunAsync(Func<Task> action)
    {
        Error = null;
        Info = null;
        IsBusy = true;
        try
        {
            await action();
        }
        catch (ApiException ex)
        {
            Error = ex.Message;
        }
        catch (Exception)
        {
            Error = "Could not reach the server.";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
