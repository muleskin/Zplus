using Avalonia.Controls;
using ZPlus.Admin.Services;
using ZPlus.Admin.ViewModels;

namespace ZPlus.AdminGui.Views;

public partial class AdminWindow : Window
{
    public AdminWindow(AdminApiClient api)
    {
        InitializeComponent();
        var viewModel = new AdminViewModel(api)
        {
            PromptForPassword = email => InputDialog.ShowAsync(this, $"New password for {email}:"),
        };
        DataContext = viewModel;
        Opened += async (_, _) => await viewModel.LoadAsync();
    }
}
