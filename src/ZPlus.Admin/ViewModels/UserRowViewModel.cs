using CommunityToolkit.Mvvm.ComponentModel;
using ZPlus.Shared.Dtos;

namespace ZPlus.Admin.ViewModels;

/// <summary>Editable row in the user management grid.</summary>
public partial class UserRowViewModel : ObservableObject
{
    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private string _role = Roles.User;
    [ObservableProperty] private bool _isDisabled;
    [ObservableProperty] private bool _mfaEnabled;
    [ObservableProperty] private bool _mfaRequired;

    public Guid Id { get; init; }
    public string Email { get; init; } = "";
    public DateTime CreatedAtUtc { get; init; }

    public string DisableButtonLabel => IsDisabled ? "Enable" : "Disable";
    public string StatusLabel => IsDisabled ? "Disabled" : "Active";
    public string MfaLabel => MfaEnabled ? "MFA on" : MfaRequired ? "MFA pending" : "MFA off";
    public string MfaButtonLabel => MfaEnabled || MfaRequired ? "Reset MFA" : "Require MFA";

    partial void OnIsDisabledChanged(bool value)
    {
        OnPropertyChanged(nameof(DisableButtonLabel));
        OnPropertyChanged(nameof(StatusLabel));
    }

    partial void OnMfaEnabledChanged(bool value) => RaiseMfaLabels();
    partial void OnMfaRequiredChanged(bool value) => RaiseMfaLabels();

    private void RaiseMfaLabels()
    {
        OnPropertyChanged(nameof(MfaLabel));
        OnPropertyChanged(nameof(MfaButtonLabel));
    }

    public static UserRowViewModel From(AdminUserDto dto) => new()
    {
        Id = dto.Id,
        Email = dto.Email,
        CreatedAtUtc = dto.CreatedAtUtc,
        DisplayName = dto.DisplayName,
        Role = dto.Role,
        IsDisabled = dto.IsDisabled,
        MfaEnabled = dto.MfaEnabled,
        MfaRequired = dto.MfaRequired,
    };

    public void Apply(AdminUserDto dto)
    {
        DisplayName = dto.DisplayName;
        Role = dto.Role;
        IsDisabled = dto.IsDisabled;
        MfaEnabled = dto.MfaEnabled;
        MfaRequired = dto.MfaRequired;
    }
}
