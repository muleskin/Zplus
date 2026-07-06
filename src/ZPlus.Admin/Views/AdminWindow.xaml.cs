using System.Windows;
using ZPlus.Admin.Services;
using ZPlus.Admin.ViewModels;

namespace ZPlus.Admin.Views;

public partial class AdminWindow : Window
{
    private readonly AdminViewModel _viewModel;

    public AdminWindow(AdminApiClient api)
    {
        InitializeComponent();
        _viewModel = new AdminViewModel(api)
        {
            PromptForPassword = email => InputDialog.Show(this, $"New password for {email}:"),
        };
        DataContext = _viewModel;
        Loaded += async (_, _) => await _viewModel.LoadAsync();
    }
}
