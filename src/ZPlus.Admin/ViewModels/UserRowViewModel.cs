using CommunityToolkit.Mvvm.ComponentModel;
using ZPlus.Shared.Dtos;

namespace ZPlus.Admin.ViewModels;

/// <summary>Editable row in the user management grid.</summary>
public partial class UserRowViewModel : ObservableObject
{
    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private string _role = Roles.User;
    [ObservableProperty] private bool _isDisabled;

    public Guid Id { get; init; }
    public string Email { get; init; } = "";
    public DateTime CreatedAtUtc { get; init; }

    public string DisableButtonLabel => IsDisabled ? "Enable" : "Disable";
    public string StatusLabel => IsDisabled ? "Disabled" : "Active";

    partial void OnIsDisabledChanged(bool value)
    {
        OnPropertyChanged(nameof(DisableButtonLabel));
        OnPropertyChanged(nameof(StatusLabel));
    }

    public static UserRowViewModel From(AdminUserDto dto) => new()
    {
        Id = dto.Id,
        Email = dto.Email,
        CreatedAtUtc = dto.CreatedAtUtc,
        DisplayName = dto.DisplayName,
        Role = dto.Role,
        IsDisabled = dto.IsDisabled,
    };

    public void Apply(AdminUserDto dto)
    {
        DisplayName = dto.DisplayName;
        Role = dto.Role;
        IsDisabled = dto.IsDisabled;
    }
}
