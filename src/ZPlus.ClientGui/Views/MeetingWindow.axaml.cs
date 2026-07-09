using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ZPlus.ClientGui.ViewModels;
using ZPlus.Shared.Dtos;

namespace ZPlus.ClientGui.Views;

public partial class MeetingWindow : Window
{
    private readonly MeetingViewModel _viewModel;

    // Whiteboard state
    private readonly List<WhiteboardStrokeDto> _boardStrokes = [];
    private string _penColor = "#FFEDEDED";
    private Canvas? _board;
    private bool _drawing;
    private readonly List<double> _livePoints = [];
    private Polyline? _livePoly;

    public MeetingWindow(string meetingCode, string? password)
    {
        InitializeComponent();
        _viewModel = new MeetingViewModel(meetingCode, password);
        DataContext = _viewModel;

        _viewModel.PickFileToUpload = PickFileAsync;
        _viewModel.PickSaveLocation = PickSaveAsync;
        _viewModel.WhiteboardStrokeReceived += s => Dispatcher.UIThread.Post(() => { _boardStrokes.Add(s); RedrawBoard(); });
        _viewModel.WhiteboardCleared += () => Dispatcher.UIThread.Post(() => { _boardStrokes.Clear(); _board?.Children.Clear(); });

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
                _boardStrokes.AddRange(_viewModel.InitialWhiteboard);
                RedrawBoard();
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

    // ---- File pickers -------------------------------------------------------

    private async Task<string?> PickFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    private async Task<string?> PickSaveAsync(string suggestedName)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { SuggestedFileName = suggestedName });
        return file?.TryGetLocalPath();
    }

    // ---- Whiteboard ---------------------------------------------------------

    private void OnPickColor(object? sender, RoutedEventArgs e)
    {
        if (sender is Control c && c.Tag is string s) _penColor = s;
    }

    private void OnBoardSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        _board = sender as Canvas;
        RedrawBoard();
    }

    private void OnBoardDown(object? sender, PointerPressedEventArgs e)
    {
        _board = sender as Canvas;
        if (_board is null) return;
        _drawing = true;
        _livePoints.Clear();
        _livePoly = new Polyline
        {
            Stroke = BrushFrom(_penColor),
            StrokeThickness = 2.5,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Points = new List<Point>(),
        };
        _board.Children.Add(_livePoly);
        AddLivePoint(e.GetPosition(_board));
    }

    private void OnBoardMove(object? sender, PointerEventArgs e)
    {
        if (!_drawing || _board is null) return;
        AddLivePoint(e.GetPosition(_board));
    }

    private async void OnBoardUp(object? sender, PointerReleasedEventArgs e)
    {
        if (!_drawing) return;
        _drawing = false;
        if (_livePoints.Count >= 4)
        {
            var stroke = new WhiteboardStrokeDto(_penColor, 2.5, [.. _livePoints]);
            _boardStrokes.Add(stroke);
            try { await _viewModel.SendStrokeAsync(stroke); } catch { /* best effort */ }
        }
        _livePoly = null;
        _livePoints.Clear();
    }

    private void AddLivePoint(Point p)
    {
        if (_board is null || _board.Bounds.Width <= 0 || _board.Bounds.Height <= 0 || _livePoly is null) return;
        double w = _board.Bounds.Width, h = _board.Bounds.Height;
        _livePoints.Add(p.X / w);
        _livePoints.Add(p.Y / h);
        var pts = new List<Point>(_livePoly.Points) { p };
        _livePoly.Points = pts;
    }

    private void RedrawBoard()
    {
        if (_board is null || _board.Bounds.Width <= 0) return;
        _board.Children.Clear();
        foreach (var s in _boardStrokes) DrawStroke(s);
    }

    private void DrawStroke(WhiteboardStrokeDto s)
    {
        if (_board is null) return;
        double w = _board.Bounds.Width, h = _board.Bounds.Height;
        var pts = new List<Point>();
        for (int i = 0; i + 1 < s.Points.Count; i += 2)
            pts.Add(new Point(s.Points[i] * w, s.Points[i + 1] * h));
        _board.Children.Add(new Polyline
        {
            Stroke = BrushFrom(s.Color),
            StrokeThickness = s.Width,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Points = pts,
        });
    }

    private static IBrush BrushFrom(string hex)
    {
        try { return new SolidColorBrush(Color.Parse(hex)); } catch { return Brushes.White; }
    }
}
