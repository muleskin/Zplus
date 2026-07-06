using System.Windows;
using System.Windows.Controls;
using ZPlus.Client.ViewModels;

namespace ZPlus.Client.Views;

public partial class LoginWindow : Window
{
    private readonly LoginViewModel _viewModel = new();

    public LoginWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.SignedIn += () =>
        {
            new HomeWindow().Show();
            Close();
        };
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e) =>
        _viewModel.Password = ((PasswordBox)sender).Password;
}
