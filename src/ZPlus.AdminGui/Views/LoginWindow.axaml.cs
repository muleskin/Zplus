using Avalonia.Controls;
using ZPlus.Admin.Services;
using ZPlus.Admin.ViewModels;

namespace ZPlus.AdminGui.Views;

public partial class LoginWindow : Window
{
    private readonly AdminApiClient _api = new();

    public LoginWindow()
    {
        InitializeComponent();
        var viewModel = new LoginViewModel(_api);
        DataContext = viewModel;
        viewModel.SignedIn += () =>
        {
            new AdminWindow(_api).Show();
            Close();
        };
    }
}
