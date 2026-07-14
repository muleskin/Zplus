using System.Collections.Specialized;
using System.Media;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using ZPlus.Client.ViewModels;
using ZPlus.Shared.Dtos;

namespace ZPlus.Client.Views;

public partial class MeetingWindow : Window
{
    private readonly MeetingViewModel _viewModel;
    private bool _exiting;

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags; public uint uCount; public uint dwTimeout; }
    [DllImport("user32.dll")] private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    // Whiteboard state
    private readonly List<WhiteboardStrokeDto> _boardStrokes = [];
    private string _penColor = "#FFEDEDED";
    private Polyline? _liveStroke;
    private readonly List<double> _livePoints = [];

    public MeetingWindow(string meetingCode, string? password)
    {
        InitializeComponent();
        _viewModel = new MeetingViewModel(meetingCode, password);
        DataContext = _viewModel;

        // File picker callbacks (WPF dialogs live in the view).
        _viewModel.PickFileToUpload = () =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog();
            return Task.FromResult(dlg.ShowDialog(this) == true ? dlg.FileName : null);
        };
        _viewModel.PickSaveLocation = name =>
        {
            var dlg = new Microsoft.Win32.SaveFileDialog { FileName = name };
            return Task.FromResult(dlg.ShowDialog(this) == true ? dlg.FileName : null);
        };

        _viewModel.WhiteboardStrokeReceived += s => Dispatcher.Invoke(() => { _boardStrokes.Add(s); RedrawBoard(); });
        _viewModel.WhiteboardCleared += () => Dispatcher.Invoke(() => { _boardStrokes.Clear(); Whiteboard.Children.Clear(); });
        _viewModel.ChatArrivedWhileAway += () => Dispatcher.Invoke(OnChatArrived);

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
                _boardStrokes.AddRange(_viewModel.InitialWhiteboard);
                RedrawBoard();
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

    // ---- Chat notification --------------------------------------------------

    private void OnSidePanelTabChanged(object sender, SelectionChangedEventArgs e)
    {
        // SelectionChanged bubbles from inner ComboBoxes/lists — only react to the tab strip.
        if (!ReferenceEquals(e.OriginalSource, SidePanelTabs)) return;
        _viewModel.SetChatTabActive(ReferenceEquals(SidePanelTabs.SelectedItem, ChatTab));
    }

    private void OnChatArrived()
    {
        try { SystemSounds.Asterisk.Play(); } catch { /* no audio device */ }
        // If the meeting window isn't the foreground window, flash its taskbar button.
        if (!IsActive)
        {
            var info = new FLASHWINFO
            {
                cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
                hwnd = new WindowInteropHelper(this).Handle,
                dwFlags = 0x3 /* FLASHW_ALL */ | 0xC /* FLASHW_TIMERNOFG */,
                uCount = uint.MaxValue,
                dwTimeout = 0,
            };
            try { FlashWindowEx(ref info); } catch { /* best effort */ }
        }
    }

    // ---- Whiteboard ---------------------------------------------------------

    private void OnPickColor(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string c) _penColor = c;
    }

    private void OnBoardLoaded(object sender, RoutedEventArgs e) => RedrawBoard();
    private void OnBoardSizeChanged(object sender, SizeChangedEventArgs e) => RedrawBoard();

    private void OnBoardDown(object sender, MouseButtonEventArgs e)
    {
        if (Whiteboard is null) return;
        _livePoints.Clear();
        _liveStroke = new Polyline
        {
            Stroke = BrushFrom(_penColor),
            StrokeThickness = 2.5,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
        Whiteboard.Children.Add(_liveStroke);
        Whiteboard.CaptureMouse();
        AddLivePoint(e.GetPosition(Whiteboard));
    }

    private void OnBoardMove(object sender, MouseEventArgs e)
    {
        if (_liveStroke is null || e.LeftButton != MouseButtonState.Pressed) return;
        AddLivePoint(e.GetPosition(Whiteboard));
    }

    private async void OnBoardUp(object sender, MouseButtonEventArgs e)
    {
        Whiteboard?.ReleaseMouseCapture();
        if (_liveStroke is null) return;
        _liveStroke = null;
        if (_livePoints.Count >= 4)
        {
            var stroke = new WhiteboardStrokeDto(_penColor, 2.5, [.. _livePoints]);
            _boardStrokes.Add(stroke);
            try { await _viewModel.SendStrokeAsync(stroke); } catch { /* best effort */ }
        }
        _livePoints.Clear();
    }

    private void AddLivePoint(Point p)
    {
        double w = Whiteboard.ActualWidth, h = Whiteboard.ActualHeight;
        if (w <= 0 || h <= 0) return;
        _livePoints.Add(p.X / w);   // normalized 0..1
        _livePoints.Add(p.Y / h);
        _liveStroke!.Points.Add(p);
    }

    private void RedrawBoard()
    {
        if (Whiteboard is null || Whiteboard.ActualWidth <= 0) return;
        Whiteboard.Children.Clear();
        foreach (var s in _boardStrokes) DrawStroke(s);
    }

    private void DrawStroke(WhiteboardStrokeDto s)
    {
        double w = Whiteboard.ActualWidth, h = Whiteboard.ActualHeight;
        var line = new Polyline
        {
            Stroke = BrushFrom(s.Color),
            StrokeThickness = s.Width,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
        for (int i = 0; i + 1 < s.Points.Count; i += 2)
            line.Points.Add(new Point(s.Points[i] * w, s.Points[i + 1] * h));
        Whiteboard.Children.Add(line);
    }

    private static Brush BrushFrom(string hex)
    {
        try { return (Brush)new BrushConverter().ConvertFromString(hex)!; }
        catch { return Brushes.White; }
    }
}
