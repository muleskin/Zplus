using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZPlus.Admin.Services;
using ZPlus.Shared.Dtos;

namespace ZPlus.Admin.ViewModels;

public partial class AdminViewModel(AdminApiClient api) : ObservableObject
{
    [ObservableProperty] private string? _status;

    // New-user form
    [ObservableProperty] private string _newEmail = "";
    [ObservableProperty] private string _newDisplayName = "";
    [ObservableProperty] private string _newPassword = "";
    [ObservableProperty] private string _newRole = Roles.User;

    // Server settings
    [ObservableProperty] private bool _allowSelfRegistration;
    [ObservableProperty] private bool _requireMeetingPasswords;
    [ObservableProperty] private string _maxParticipants = "25";
    [ObservableProperty] private string _listenUrl = "";
    [ObservableProperty] private string _publicUrl = "";
    [ObservableProperty] private string _smtpHost = "";
    [ObservableProperty] private string _smtpPort = "587";
    [ObservableProperty] private string _smtpFrom = "";
    [ObservableProperty] private string _smtpUser = "";
    [ObservableProperty] private string _smtpPassword = "";
    [ObservableProperty] private string _testRecipient = "";

    public ObservableCollection<UserRowViewModel> Users { get; } = [];
    public ObservableCollection<ActiveMeetingDto> ActiveMeetings { get; } = [];
    public string[] AvailableRoles => Roles.All;

    public string HeaderText =>
        $"Signed in as {api.SignedInUser?.DisplayName} ({api.SignedInUser?.Role}) — {api.ServerUrl}";

    /// <summary>Set by the view; asks the operator for a new password when resetting one.</summary>
    public Func<string, Task<string?>>? PromptForPassword { get; set; }

    public async Task LoadAsync()
    {
        await RunAsync(async () =>
        {
            await RefreshUsersAsync();
            await RefreshSettingsAsync();
            await RefreshMeetingsAsync();
            Status = "Loaded.";
        });
    }

    // ---- Users -------------------------------------------------------------

    [RelayCommand]
    private async Task RefreshUsersAsync()
    {
        var users = await api.GetUsersAsync();
        Users.Clear();
        foreach (var u in users) Users.Add(UserRowViewModel.From(u));
    }

    [RelayCommand]
    private async Task CreateUserAsync()
    {
        await RunAsync(async () =>
        {
            var created = await api.CreateUserAsync(
                new AdminCreateUserRequest(NewEmail, NewDisplayName, NewPassword, NewRole));
            Users.Add(UserRowViewModel.From(created));
            Status = $"Created {created.Email}.";
            NewEmail = "";
            NewDisplayName = "";
            NewPassword = "";
            NewRole = Roles.User;
        });
    }

    [RelayCommand]
    private async Task SaveUserAsync(UserRowViewModel? row)
    {
        if (row is null) return;
        await RunAsync(async () =>
        {
            var updated = await api.UpdateUserAsync(row.Id, new AdminUpdateUserRequest(row.DisplayName, row.Role, null));
            row.Apply(updated);
            Status = $"Saved {row.Email}.";
        });
    }

    [RelayCommand]
    private async Task ToggleDisabledAsync(UserRowViewModel? row)
    {
        if (row is null) return;
        await RunAsync(async () =>
        {
            var updated = await api.UpdateUserAsync(row.Id, new AdminUpdateUserRequest(null, null, !row.IsDisabled));
            row.Apply(updated);
            Status = $"{row.Email} is now {(row.IsDisabled ? "disabled" : "enabled")}.";
        });
    }

    [RelayCommand]
    private async Task ResetPasswordAsync(UserRowViewModel? row)
    {
        if (row is null || PromptForPassword is null) return;
        var newPassword = await PromptForPassword(row.Email);
        if (string.IsNullOrEmpty(newPassword)) return;
        await RunAsync(async () =>
        {
            await api.ResetPasswordAsync(row.Id, newPassword);
            Status = $"Password reset for {row.Email}.";
        });
    }

    // ---- Settings ------------------------------------------------------------

    [RelayCommand]
    private async Task RefreshSettingsAsync()
    {
        var settings = await api.GetSettingsAsync();
        AllowSelfRegistration = settings.AllowSelfRegistration;
        RequireMeetingPasswords = settings.RequireMeetingPasswords;
        MaxParticipants = settings.MaxParticipantsPerMeeting.ToString();
        ListenUrl = settings.ListenUrl;
        PublicUrl = settings.PublicUrl;
        SmtpHost = settings.SmtpHost;
        SmtpPort = settings.SmtpPort.ToString();
        SmtpFrom = settings.SmtpFrom;
        SmtpUser = settings.SmtpUser;
        SmtpPassword = "";
        if (string.IsNullOrEmpty(TestRecipient))
            TestRecipient = api.SignedInUser?.Email ?? "";
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        if (!int.TryParse(MaxParticipants, out var max))
        {
            Status = "Max participants must be a number.";
            return;
        }
        if (!int.TryParse(SmtpPort, out var smtpPort))
        {
            Status = "SMTP port must be a number.";
            return;
        }
        await RunAsync(async () =>
        {
            var saved = await api.SaveSettingsAsync(new ServerSettingsDto(
                AllowSelfRegistration, RequireMeetingPasswords, max, ListenUrl.Trim(),
                PublicUrl.Trim(), SmtpHost.Trim(), smtpPort, SmtpFrom.Trim(), SmtpUser.Trim(),
                SmtpPassword));
            AllowSelfRegistration = saved.AllowSelfRegistration;
            RequireMeetingPasswords = saved.RequireMeetingPasswords;
            MaxParticipants = saved.MaxParticipantsPerMeeting.ToString();
            ListenUrl = saved.ListenUrl;
            PublicUrl = saved.PublicUrl;
            SmtpHost = saved.SmtpHost;
            SmtpPort = saved.SmtpPort.ToString();
            SmtpFrom = saved.SmtpFrom;
            SmtpUser = saved.SmtpUser;
            SmtpPassword = "";
            Status = "Settings saved. Listen URL changes take effect after a server restart.";
        });
    }

    [RelayCommand]
    private async Task TestEmailAsync()
    {
        if (!int.TryParse(SmtpPort, out var smtpPort))
        {
            Status = "SMTP port must be a number.";
            return;
        }
        if (string.IsNullOrWhiteSpace(TestRecipient))
        {
            Status = "Enter a test recipient email address.";
            return;
        }
        Status = $"Sending test email to {TestRecipient.Trim()}…";
        await RunAsync(async () =>
        {
            // Test the settings currently shown in the form (unsaved is fine); a blank
            // password reuses the one already stored on the server.
            var settings = new ServerSettingsDto(
                AllowSelfRegistration, RequireMeetingPasswords,
                int.TryParse(MaxParticipants, out var max) ? max : 25, ListenUrl.Trim(),
                PublicUrl.Trim(), SmtpHost.Trim(), smtpPort, SmtpFrom.Trim(), SmtpUser.Trim(), SmtpPassword);
            await api.SendTestEmailAsync(settings, TestRecipient.Trim());
            Status = $"Test email sent to {TestRecipient.Trim()}. Check that inbox.";
        });
    }

    // ---- Meetings ----------------------------------------------------------------

    [RelayCommand]
    private async Task RefreshMeetingsAsync()
    {
        var meetings = await api.GetActiveMeetingsAsync();
        ActiveMeetings.Clear();
        foreach (var m in meetings) ActiveMeetings.Add(m);
    }

    [RelayCommand]
    private async Task ForceEndMeetingAsync(ActiveMeetingDto? meeting)
    {
        if (meeting is null) return;
        await RunAsync(async () =>
        {
            await api.ForceEndMeetingAsync(meeting.Id);
            ActiveMeetings.Remove(meeting);
            Status = $"Ended \"{meeting.Topic}\".";
        });
    }

    // ---- Helpers -----------------------------------------------------------------

    private async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (ApiException ex)
        {
            Status = ex.Message;
        }
        catch (Exception)
        {
            Status = "Could not reach the server.";
        }
    }
}
