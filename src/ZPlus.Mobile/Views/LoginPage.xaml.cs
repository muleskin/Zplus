using ZPlus.Client.ViewModels;

namespace ZPlus.Mobile.Views;

public partial class LoginPage : ContentPage
{
    private readonly LoginViewModel _viewModel = new();

    public LoginPage()
    {
        InitializeComponent();
        BindingContext = _viewModel;
        _viewModel.SignedIn += async () =>
            await Navigation.PushAsync(new HomePage());
    }
}
