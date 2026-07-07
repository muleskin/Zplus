using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using ZPlus.ClientGui.ViewModels;

namespace ZPlus.ClientGui.Views;

public partial class MeetingWindow : Window
{
    private readonly MeetingViewModel _viewModel;

    public MeetingWindow(string meetingCode, string? password)
    {
        InitializeComponent();
        _viewModel = new MeetingViewModel(meetingCode, password);
        DataContext = _viewModel;

        _viewModel.MeetingExited += reason =>
        {
            if (reason is not null) _viewModel.StatusMessage = reason;
            Close();
        };
        _viewModel.ChatMessages.CollectionChanged += OnChatChanged;

        Opened += async (_, _) =>
        {
            try
            {
                await _viewModel.InitializeAsync();
            }
            catch (Exception ex)
            {
                _viewModel.StatusMessage = ex.Message.Replace("HubException: ", "");
                _viewModel.Title = "Could not join meeting";
            }
        };
        Closing += async (_, _) => await _viewModel.ShutdownAsync();
    }

    private void OnChatChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        this.FindControl<ScrollViewer>("ChatScroll")?.ScrollToEnd();

    private void OnChatKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _viewModel.SendChatCommand.CanExecute(null))
        {
            _viewModel.SendChatCommand.Execute(null);
        }
    }
}
