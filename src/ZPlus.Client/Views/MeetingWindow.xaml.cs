using System.Collections.Specialized;
using System.Windows;
using System.Windows.Input;
using ZPlus.Client.ViewModels;

namespace ZPlus.Client.Views;

public partial class MeetingWindow : Window
{
    private readonly MeetingViewModel _viewModel;
    private bool _exiting;

    public MeetingWindow(string meetingCode, string? password)
    {
        InitializeComponent();
        _viewModel = new MeetingViewModel(meetingCode, password);
        DataContext = _viewModel;

        _viewModel.MeetingExited += reason =>
        {
            _exiting = true;
            if (reason is not null)
            {
                MessageBox.Show(this, reason, "Z+", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            Close();
        };
        _viewModel.ChatMessages.CollectionChanged += OnChatChanged;
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MeetingViewModel.IsMuted))
                MuteButton.Content = _viewModel.IsMuted ? "Unmute" : "Mute";
            else if (e.PropertyName == nameof(MeetingViewModel.IsVideoOn))
                VideoButton.Content = _viewModel.IsVideoOn ? "Stop video" : "Start video";
        };

        Loaded += async (_, _) =>
        {
            try
            {
                await _viewModel.InitializeAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message.Replace("HubException: ", ""), "Could not join meeting",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                _exiting = true;
                Close();
            }
        };

        Closing += async (_, e) =>
        {
            if (!_exiting)
            {
                // Treat closing the window as leaving the meeting.
                _exiting = true;
            }
            await _viewModel.ShutdownAsync();
        };
    }

    private void OnChatChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        ChatScroll.ScrollToBottom();

    private void OnChatKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _viewModel.SendChatCommand.CanExecute(null))
        {
            _viewModel.SendChatCommand.Execute(null);
        }
    }
}
