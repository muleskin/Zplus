using System.Collections.Specialized;
using Microsoft.Maui.Graphics;
using ZPlus.ClientGui.ViewModels;
using ZPlus.Shared.Dtos;

namespace ZPlus.Mobile.Views;

public partial class MeetingPage : ContentPage
{
    private readonly MeetingViewModel _viewModel;
    private readonly WhiteboardDrawable _drawable = new();
    private string _penColor = "#FFEDEDED";
    private WhiteboardStrokeDto? _liveStroke;
    private bool _started;

    public MeetingPage(string meetingCode, string? password)
    {
        InitializeComponent();
        _viewModel = new MeetingViewModel(meetingCode, password);
        BindingContext = _viewModel;

        Board.Drawable = _drawable;

        // File pick/save (MAUI Essentials).
        _viewModel.PickFileToUpload = async () =>
        {
            var result = await FilePicker.Default.PickAsync();
            return result?.FullPath;
        };
        _viewModel.PickSaveLocation = name =>
            Task.FromResult<string?>(Path.Combine(FileSystem.CacheDirectory, name));

        _viewModel.WhiteboardStrokeReceived += s => { _drawable.Strokes.Add(s); Board.Invalidate(); };
        _viewModel.WhiteboardCleared += () => { _drawable.Strokes.Clear(); Board.Invalidate(); };

        _viewModel.ChatMessages.CollectionChanged += OnChatChanged;
        _viewModel.MeetingExited += async reason =>
        {
            if (reason is not null) await DisplayAlertAsync("Z+", reason, "OK");
            await Navigation.PopAsync();
        };

        ShowTab(PeopleTab);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_started) return;
        _started = true;
        try
        {
            await _viewModel.InitializeAsync();
            _drawable.Strokes.AddRange(_viewModel.InitialWhiteboard);
            Board.Invalidate();
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Could not join meeting", ex.Message.Replace("HubException: ", ""), "OK");
            await Navigation.PopAsync();
        }
    }

    protected override async void OnDisappearing()
    {
        base.OnDisappearing();
        await _viewModel.ShutdownAsync();
    }

    private void OnChatChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_viewModel.ChatMessages.Count > 0)
            ChatList.ScrollTo(_viewModel.ChatMessages.Count - 1, animate: false);
    }

    // ---- Tabs ---------------------------------------------------------------

    private void OnTabPeople(object? s, EventArgs e) => ShowTab(PeopleTab);
    private void OnTabChat(object? s, EventArgs e) => ShowTab(ChatTab);
    private void OnTabPolls(object? s, EventArgs e) => ShowTab(PollsTab);
    private void OnTabFiles(object? s, EventArgs e) => ShowTab(FilesTab);
    private void OnTabBoard(object? s, EventArgs e) => ShowTab(BoardTab);
    private void OnTabRooms(object? s, EventArgs e) => ShowTab(RoomsTab);

    private void ShowTab(View tab)
    {
        PeopleTab.IsVisible = ReferenceEquals(tab, PeopleTab);
        ChatTab.IsVisible = ReferenceEquals(tab, ChatTab);
        PollsTab.IsVisible = ReferenceEquals(tab, PollsTab);
        FilesTab.IsVisible = ReferenceEquals(tab, FilesTab);
        BoardTab.IsVisible = ReferenceEquals(tab, BoardTab);
        RoomsTab.IsVisible = ReferenceEquals(tab, RoomsTab);
        _viewModel.SetChatTabActive(ReferenceEquals(tab, ChatTab));
    }

    // ---- Whiteboard ---------------------------------------------------------

    private void OnPickColor(object? sender, EventArgs e)
    {
        if (sender is Button b && b.CommandParameter is string c) _penColor = c;
    }

    private void OnBoardStart(object? sender, TouchEventArgs e)
    {
        if (e.Touches.Length == 0) return;
        _liveStroke = new WhiteboardStrokeDto(_penColor, 2.5, []);
        _drawable.Strokes.Add(_liveStroke);
        AddPoint(e.Touches[0]);
    }

    private void OnBoardDrag(object? sender, TouchEventArgs e)
    {
        if (_liveStroke is null || e.Touches.Length == 0) return;
        AddPoint(e.Touches[0]);
    }

    private async void OnBoardEnd(object? sender, TouchEventArgs e)
    {
        var stroke = _liveStroke;
        _liveStroke = null;
        if (stroke is null) return;
        if (stroke.Points.Count >= 4)
        {
            try { await _viewModel.SendStrokeAsync(stroke); } catch { /* best effort */ }
        }
    }

    private void AddPoint(PointF p)
    {
        double w = Board.Width, h = Board.Height;
        if (w <= 0 || h <= 0 || _liveStroke is null) return;
        _liveStroke.Points.Add(p.X / w);
        _liveStroke.Points.Add(p.Y / h);
        Board.Invalidate();
    }
}
