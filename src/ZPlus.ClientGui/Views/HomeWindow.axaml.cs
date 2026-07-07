using Avalonia.Controls;
using ZPlus.Client.ViewModels;

namespace ZPlus.ClientGui.Views;

public partial class HomeWindow : Window
{
    private readonly HomeViewModel _viewModel = new();

    public HomeWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.OpenMeetingRequested += (code, password) =>
        {
            new MeetingWindow(code, password).Show();
        };
        Opened += async (_, _) => await _viewModel.RefreshMeetingsAsync();
        Activated += async (_, _) => await _viewModel.RefreshMeetingsAsync();
    }
}
