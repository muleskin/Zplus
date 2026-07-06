using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZPlus.Admin.Services;

namespace ZPlus.Admin.ViewModels;

public partial class LoginViewModel(AdminApiClient api) : ObservableObject
{
    [ObservableProperty] private string _serverUrl = "http://localhost:5199";
    [ObservableProperty] private string _email = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _error;

    public event Action? SignedIn;

    [RelayCommand]
    private async Task SignInAsync()
    {
        Error = null;
        IsBusy = true;
        try
        {
            api.ServerUrl = ServerUrl.Trim();
            await api.SignInAsync(Email.Trim(), Password);
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
