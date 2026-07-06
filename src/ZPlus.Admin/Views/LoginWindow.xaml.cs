using System.Windows;
using System.Windows.Controls;
using ZPlus.Admin.Services;
using ZPlus.Admin.ViewModels;

namespace ZPlus.Admin.Views;

public partial class LoginWindow : Window
{
    private readonly AdminApiClient _api = new();
    private readonly LoginViewModel _viewModel;

    public LoginWindow()
    {
        InitializeComponent();
        _viewModel = new LoginViewModel(_api);
        DataContext = _viewModel;
        _viewModel.SignedIn += () =>
        {
            new AdminWindow(_api).Show();
            Close();
        };
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e) =>
        _viewModel.Password = ((PasswordBox)sender).Password;
}
