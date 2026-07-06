using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZPlus.Client.Services;
using ZPlus.Shared.Dtos;

namespace ZPlus.Client.ViewModels;

public partial class LoginViewModel : ObservableObject
{
    private readonly ApiClient _api = new();

    [ObservableProperty] private string _email = "";
    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _serverUrl = AppSession.Current.ServerUrl;
    [ObservableProperty] private bool _isRegisterMode;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _error;

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
            var auth = IsRegisterMode
                ? await _api.RegisterAsync(new RegisterRequest(Email, DisplayName, Password))
                : await _api.LoginAsync(new LoginRequest(Email, Password));

            AppSession.Current.Token = auth.Token;
            AppSession.Current.User = auth.User;
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
