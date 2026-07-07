using Avalonia.Controls;
using ZPlus.Client.ViewModels;

namespace ZPlus.ClientGui.Views;

public partial class LoginWindow : Window
{
    public LoginWindow()
    {
        InitializeComponent();
        var viewModel = new LoginViewModel();
        DataContext = viewModel;
        viewModel.SignedIn += () =>
        {
            new HomeWindow().Show();
            Close();
        };
    }
}
