using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZPlus.Client.Services;
using ZPlus.Shared.Dtos;

namespace ZPlus.Client.ViewModels;

public partial class LoginViewModel : ObservableObject
{
    private readonly ApiClient _api = new();

    [ObservableProperty] private string _email = LocalSettings.Current.Email;
    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _serverUrl = AppSession.Current.ServerUrl;
    [ObservableProperty] private bool _isRegisterMode;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _error;

    // Multi-factor authentication (shown only when the server asks for it).
    [ObservableProperty] private bool _mfaRequired;
    [ObservableProperty] private string _mfaCode = "";
    [ObservableProperty] private string? _mfaSecret;
    [ObservableProperty] private string? _mfaUri;
    [ObservableProperty] private string? _mfaInfo;

    /// <summary>Raised after a successful sign-in or registration.</summary>
    public event Action? SignedIn;

    public string SubmitLabel => IsRegisterMode ? "Create account" : "Sign in";
    public string ToggleLabel => IsRegisterMode ? "Have an account? Sign in" : "New here? Create an account";

    partial void OnIsRegisterModeChanged(bool value)
    {
        OnPropertyChanged(nameof(SubmitLabel));
        OnPropertyChanged(nameof(ToggleLabel));
    }

    [RelayCommand]
    private void ToggleMode()
    {
        Error = null;
        MfaRequired = false;
        MfaCode = "";
        MfaSecret = null;
        MfaUri = null;
        MfaInfo = null;
        IsRegisterMode = !IsRegisterMode;
    }

    [RelayCommand]
    private async Task SubmitAsync()
    {
        Error = null;
        IsBusy = true;
        try
        {
            AppSession.Current.ServerUrl = ServerUrl.Trim();
            AuthResponse auth;
            if (IsRegisterMode)
            {
                auth = await _api.RegisterAsync(new RegisterRequest(Email, DisplayName, Password));
            }
            else
            {
                var code = string.IsNullOrWhiteSpace(MfaCode) ? null : MfaCode.Trim();
                auth = await _api.LoginAsync(new LoginRequest(Email.Trim(), Password, code, MfaSecret));
            }

            // A null token means the server wants a second factor before issuing one.
            if (auth.Token is null)
            {
                MfaRequired = true;
                if (auth.Enrollment is not null)
                {
                    MfaSecret = auth.Enrollment.Secret;
                    MfaUri = auth.Enrollment.OtpauthUri;
                    MfaInfo = "Set up two-factor sign-in: add this key to your authenticator app " +
                              "(Google Authenticator, Authy, 1Password…), then enter the 6-digit code.";
                }
                else
                {
                    MfaInfo = "Enter the 6-digit code from your authenticator app.";
                }
                return;
            }

            AppSession.Current.Token = auth.Token;
            AppSession.Current.User = auth.User;

            // Remember the server and email for next launch.
            LocalSettings.Current.ServerUrl = AppSession.Current.ServerUrl;
            LocalSettings.Current.Email = Email.Trim();
            LocalSettings.Current.Save();

            SignedIn?.Invoke();
        }
        catch (ApiException ex)
        {
            Error = ex.Message;
        }
        catch (Exception)
        {
            Error = "Could not reach the server. Is it running?";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
