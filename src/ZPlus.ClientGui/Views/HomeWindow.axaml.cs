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
        Opened += async (_, _) =>
        {
            await _viewModel.RefreshMeetingsAsync();
            // If we arrived here from an invitation link, jump straight into the meeting.
            await _viewModel.TryPendingJoinAsync();
        };
        Activated += async (_, _) => await _viewModel.RefreshMeetingsAsync();
    }

    /// <summary>Called when an invitation link arrives while this window is open.</summary>
    public Task HandlePendingJoinAsync() => _viewModel.TryPendingJoinAsync();
}
