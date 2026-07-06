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

    public ObservableCollection<UserRowViewModel> Users { get; } = [];
    public ObservableCollection<ActiveMeetingDto> ActiveMeetings { get; } = [];
    public string[] AvailableRoles => Roles.All;

    public string HeaderText =>
        $"Signed in as {api.SignedInUser?.DisplayName} ({api.SignedInUser?.Role}) — {api.ServerUrl}";

    /// <summary>Set by the view; asks the operator for a new password when resetting one.</summary>
    public Func<string, string?>? PromptForPassword { get; set; }

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
        if (row is null) return;
        var newPassword = PromptForPassword?.Invoke(row.Email);
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
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        if (!int.TryParse(MaxParticipants, out var max))
        {
            Status = "Max participants must be a number.";
            return;
        }
        await RunAsync(async () =>
        {
            var saved = await api.SaveSettingsAsync(
                new ServerSettingsDto(AllowSelfRegistration, RequireMeetingPasswords, max, ListenUrl.Trim()));
            AllowSelfRegistration = saved.AllowSelfRegistration;
            RequireMeetingPasswords = saved.RequireMeetingPasswords;
            MaxParticipants = saved.MaxParticipantsPerMeeting.ToString();
            ListenUrl = saved.ListenUrl;
            Status = "Settings saved. Listen URL changes take effect after a server restart.";
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
