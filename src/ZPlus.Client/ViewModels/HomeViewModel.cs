using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZPlus.Client.Services;
using ZPlus.Shared.Dtos;

namespace ZPlus.Client.ViewModels;

/// <summary>A recurrence choice shown in the schedule form (Label for display, Value sent to the server).</summary>
public record RepeatOption(string Label, string Value);

/// <summary>A reminder lead time: how long before each occurrence to email invitations.</summary>
public record ReminderOption(string Label, int Minutes);

/// <summary>How the user wants to type the start time.</summary>
public record TimeFormatOption(string Label, bool Is24Hour);

public partial class HomeViewModel : ObservableObject
{
    private readonly ApiClient _api = new();

    private static readonly RepeatOption[] _repeatOptions =
    [
        new("Does not repeat", "None"),
        new("Daily", "Daily"),
        new("Weekly", "Weekly"),
        new("Monthly", "Monthly"),
    ];
    private static readonly ReminderOption[] _reminderOptions =
    [
        new("Send invites now", 0),
        new("15 minutes before", 15),
        new("1 hour before", 60),
        new("1 day before", 1440),
        new("2 days before", 2880),
        new("1 week before", 10080),
    ];

    private static readonly TimeFormatOption[] _timeFormats =
    [
        new("12-hour (AM/PM)", false),
        new("24-hour", true),
    ];

    public RepeatOption[] RepeatOptions => _repeatOptions;
    public ReminderOption[] ReminderOptions => _reminderOptions;
    public TimeFormatOption[] TimeFormats => _timeFormats;
    public string[] Meridiems => ["AM", "PM"];

    /// <summary>Every time zone on this machine; the meeting time is interpreted in the selected one.</summary>
    public IReadOnlyList<TimeZoneInfo> TimeZones { get; } = TimeZoneInfo.GetSystemTimeZones();

    public HomeViewModel()
    {
        _selectedRepeat = _repeatOptions[0];
        _selectedReminder = _reminderOptions[0];

        // Default to whatever this machine's culture uses, so the field feels familiar.
        bool culture12Hour = CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern
            .Contains('t', StringComparison.OrdinalIgnoreCase);
        _selectedTimeFormat = culture12Hour ? _timeFormats[0] : _timeFormats[1];
        _scheduleTime = culture12Hour ? "9:00" : "09:00";

        // Pick the instance from the list (not TimeZoneInfo.Local) so pickers match by reference.
        _selectedTimeZone = TimeZones.FirstOrDefault(z => z.Id == TimeZoneInfo.Local.Id)
            ?? TimeZones.FirstOrDefault()
            ?? TimeZoneInfo.Utc;
    }

    [ObservableProperty] private string _joinCode = "";
    [ObservableProperty] private string _joinPassword = "";
    [ObservableProperty] private string _scheduleTopic = "";
    [ObservableProperty] private string _schedulePassword = "";
    [ObservableProperty] private string _scheduleInvites = "";
    [ObservableProperty] private DateTime _scheduleDate = DateTime.Today.AddDays(1);
    [ObservableProperty] private string _scheduleTime;
    [ObservableProperty] private string _scheduleDuration = "60";
    [ObservableProperty] private bool _waitingRoomEnabled;
    [ObservableProperty] private RepeatOption _selectedRepeat;
    [ObservableProperty] private string _recurrenceCount = "4";
    [ObservableProperty] private ReminderOption _selectedReminder;
    [ObservableProperty] private bool _isBusy;

    // Time entry: 12-hour (with an AM/PM picker) or 24-hour, in a chosen time zone.
    [ObservableProperty] private TimeFormatOption _selectedTimeFormat;
    [ObservableProperty] private string _selectedMeridiem = "AM";
    [ObservableProperty] private TimeZoneInfo _selectedTimeZone;

    /// <summary>True when the AM/PM picker applies.</summary>
    public bool Is12HourTime => !SelectedTimeFormat.Is24Hour;
    public string TimeHint => SelectedTimeFormat.Is24Hour ? "Time (24-hour, e.g. 14:30)" : "Time (e.g. 2:30)";

    partial void OnSelectedTimeFormatChanged(TimeFormatOption? oldValue, TimeFormatOption newValue)
    {
        OnPropertyChanged(nameof(Is12HourTime));
        OnPropertyChanged(nameof(TimeHint));
        if (oldValue is null) return;

        // Re-render whatever they already typed in the newly chosen format.
        if (!TryParseTime(ScheduleTime, oldValue, SelectedMeridiem, out var t)) return;
        if (newValue.Is24Hour)
        {
            ScheduleTime = $"{t.Hours:D2}:{t.Minutes:D2}";
        }
        else
        {
            int h12 = t.Hours % 12;
            if (h12 == 0) h12 = 12;
            ScheduleTime = $"{h12}:{t.Minutes:D2}";
            SelectedMeridiem = t.Hours < 12 ? "AM" : "PM";
        }
    }

    /// <summary>Parses the typed start time according to the chosen format.</summary>
    private static bool TryParseTime(string? text, TimeFormatOption format, string meridiem, out TimeSpan time)
    {
        time = default;
        text = (text ?? "").Trim();
        if (text.Length == 0) return false;

        if (format.Is24Hour)
        {
            return TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out time)
                   && time >= TimeSpan.Zero && time < TimeSpan.FromDays(1);
        }

        // 12-hour: the AM/PM picker supplies the suffix unless it was typed inline.
        bool hasSuffix = text.Contains("AM", StringComparison.OrdinalIgnoreCase)
                         || text.Contains("PM", StringComparison.OrdinalIgnoreCase);
        var combined = hasSuffix ? text : $"{text} {meridiem}";
        string[] patterns = ["h:mm tt", "hh:mm tt", "h:mmtt", "hh:mmtt", "h tt", "hh tt"];
        if (DateTime.TryParseExact(combined, patterns, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            time = dt.TimeOfDay;
            return true;
        }
        return false;
    }
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
            var created = await _api.CreateMeetingAsync(
                new CreateMeetingRequest(topic, null, null, null, null, WaitingRoomEnabled));
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
        if (!TryParseTime(ScheduleTime, SelectedTimeFormat, SelectedMeridiem, out var time))
        {
            Error = SelectedTimeFormat.Is24Hour
                ? "Enter the start time in 24-hour form, e.g. 14:30."
                : "Enter the start time as h:mm and pick AM or PM, e.g. 2:30 PM.";
            return;
        }
        if (!int.TryParse(ScheduleDuration, out var duration) || duration <= 0)
        {
            Error = "Enter the duration in minutes.";
            return;
        }
        // Interpret the typed date/time in the selected time zone, not the machine's.
        var localStart = DateTime.SpecifyKind(ScheduleDate.Date + time, DateTimeKind.Unspecified);
        var zone = SelectedTimeZone ?? TimeZoneInfo.Local;
        DateTime startUtc;
        try
        {
            startUtc = TimeZoneInfo.ConvertTimeToUtc(localStart, zone);
        }
        catch (ArgumentException)
        {
            // The clock skips this time in that zone (daylight-saving spring-forward).
            Error = $"{localStart:t} doesn't exist on that date in {zone.DisplayName} (clocks change). Pick another time.";
            return;
        }

        var invites = ScheduleInvites
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        await RunAsync(async () =>
        {
            var password = string.IsNullOrEmpty(SchedulePassword) ? null : SchedulePassword;
            var pattern = SelectedRepeat?.Value ?? "None";
            var count = int.TryParse(RecurrenceCount, out var rc) ? rc : 1;
            var lead = SelectedReminder?.Minutes ?? 0;
            var created = await _api.CreateMeetingAsync(
                new CreateMeetingRequest(ScheduleTopic.Trim(), password, startUtc, duration,
                    invites.Count > 0 ? invites : null, WaitingRoomEnabled, pattern, count, lead,
                    zone.Id, SelectedTimeFormat.Is24Hour));

            Info = $"Scheduled \"{created.Meeting.Topic}\"";
            if (created.OccurrencesCreated > 1)
                Info += $" — {created.OccurrencesCreated} occurrences";
            Info += $" · meeting ID {created.Meeting.MeetingCode}";
            if (created.InvitesSent > 0)
                Info += $" · {created.InvitesSent} invitation(s) emailed";
            if (created.InvitesQueued > 0)
                Info += $" · {created.InvitesQueued} reminder(s) scheduled";
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
