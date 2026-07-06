using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using SIPSorceryMedia.Abstractions;
using ZPlus.Client.Media;
using ZPlus.Shared.Dtos;

namespace ZPlus.Client.ViewModels;

/// <summary>One tile in the video grid: a participant plus their live video surface.</summary>
public partial class ParticipantTileViewModel : ObservableObject
{
    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private bool _isHost;
    [ObservableProperty] private bool _isMuted;
    [ObservableProperty] private bool _isVideoOn;
    [ObservableProperty] private ImageSource? _videoSource;

    private WriteableBitmap? _bitmap;

    public Guid UserId { get; init; }
    public string ConnectionId { get; init; } = "";
    public bool IsSelf { get; init; }

    public string Initials
    {
        get
        {
            var parts = DisplayName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length switch
            {
                0 => "?",
                1 => parts[0][..1].ToUpperInvariant(),
                _ => $"{parts[0][0]}{parts[^1][0]}".ToUpperInvariant(),
            };
        }
    }

    public static ParticipantTileViewModel From(ParticipantDto dto, bool isSelf = false) => new()
    {
        UserId = dto.UserId,
        ConnectionId = dto.ConnectionId,
        IsSelf = isSelf,
        DisplayName = isSelf ? $"{dto.DisplayName} (You)" : dto.DisplayName,
        IsHost = dto.IsHost,
        IsMuted = dto.IsMuted,
        IsVideoOn = dto.IsVideoOn,
    };

    public void ApplyState(ParticipantDto dto)
    {
        IsHost = dto.IsHost;
        IsMuted = dto.IsMuted;
        IsVideoOn = dto.IsVideoOn;
    }

    /// <summary>Renders a decoded frame into this tile. Must be called on the UI thread.</summary>
    public void RenderFrame(VideoFrame frame)
    {
        var (format, bytesPerPixel) = frame.PixelFormat switch
        {
            VideoPixelFormatsEnum.Rgb => (PixelFormats.Rgb24, 3),
            VideoPixelFormatsEnum.Bgra => (PixelFormats.Bgra32, 4),
            _ => (PixelFormats.Bgr24, 3),
        };

        if (_bitmap is null || _bitmap.PixelWidth != frame.Width || _bitmap.PixelHeight != frame.Height ||
            _bitmap.Format != format)
        {
            _bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, format, null);
            VideoSource = _bitmap;
        }

        int stride = frame.Width * bytesPerPixel;
        _bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Sample, stride, 0);
    }

    public void ClearVideo()
    {
        _bitmap = null;
        VideoSource = null;
    }
}
