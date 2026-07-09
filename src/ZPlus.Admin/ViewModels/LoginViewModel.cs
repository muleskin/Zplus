using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZPlus.Admin.Services;

namespace ZPlus.Admin.ViewModels;

public partial class LoginViewModel(AdminApiClient api) : ObservableObject
{
    [ObservableProperty] private string _serverUrl = LocalSettings.Current.ServerUrl;
    [ObservableProperty] private string _email = LocalSettings.Current.Email;
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _error;

    // Multi-factor authentication (shown only when the server asks for it).
    [ObservableProperty] private bool _mfaRequired;
    [ObservableProperty] private string _mfaCode = "";
    [ObservableProperty] private string? _mfaSecret;
    [ObservableProperty] private string? _mfaUri;
    [ObservableProperty] private string? _mfaInfo;

    public event Action? SignedIn;

    [RelayCommand]
    private async Task SignInAsync()
    {
        Error = null;
        IsBusy = true;
        try
        {
            api.ServerUrl = ServerUrl.Trim();
            var code = string.IsNullOrWhiteSpace(MfaCode) ? null : MfaCode.Trim();
            var auth = await api.SignInAsync(Email.Trim(), Password, code, MfaSecret);

            if (auth.Token is null)
            {
                MfaRequired = true;
                if (auth.Enrollment is not null)
                {
                    MfaSecret = auth.Enrollment.Secret;
                    MfaUri = auth.Enrollment.OtpauthUri;
                    MfaInfo = "Set up two-factor sign-in: add this key to your authenticator app, then enter the 6-digit code.";
                }
                else
                {
                    MfaInfo = "Enter the 6-digit code from your authenticator app.";
                }
                return;
            }

            // Remember the server and email for next launch.
            LocalSettings.Current.ServerUrl = ServerUrl.Trim();
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
