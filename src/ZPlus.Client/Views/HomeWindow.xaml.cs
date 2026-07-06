using System.Windows;
using ZPlus.Client.ViewModels;

namespace ZPlus.Client.Views;

public partial class HomeWindow : Window
{
    private readonly HomeViewModel _viewModel = new();

    public HomeWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.OpenMeetingRequested += (code, password) =>
        {
            var meetingWindow = new MeetingWindow(code, password);
            meetingWindow.Show();
        };
        Loaded += async (_, _) => await _viewModel.RefreshMeetingsAsync();
        Activated += async (_, _) => await _viewModel.RefreshMeetingsAsync();
    }
}
