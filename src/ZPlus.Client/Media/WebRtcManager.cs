using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;
using SIPSorceryMedia.Windows;
using ZPlus.Client.Services;

namespace ZPlus.Client.Media;

/// <summary>A decoded video frame ready to be rendered.</summary>
public record VideoFrame(byte[] Sample, int Width, int Height, VideoPixelFormatsEnum PixelFormat);

/// <summary>
/// Manages WebRTC media for a meeting using a full-mesh topology: one peer connection
/// per remote participant, with a single shared microphone and camera source.
/// The joining client initiates the offer to every participant already in the room.
/// </summary>
public class WebRtcManager : IAsyncDisposable
{
    private static readonly RTCConfiguration Config = new()
    {
        iceServers = [new RTCIceServer { urls = "stun:stun.l.google.com:19302" }],
    };

    private readonly MeetingHubClient _hub;
    private readonly ConcurrentDictionary<string, Peer> _peers = new();

    private WindowsAudioEndPoint? _micSource;
    private WindowsVideoEndPoint? _cameraSource;
    private bool _micStarted;
    private bool _cameraStarted;
    private bool _disposed;

    /// <summary>Raised with each local camera frame for self-view rendering (worker thread).</summary>
    public event Action<VideoFrame>? LocalVideoFrame;

    /// <summary>Raised with each decoded remote frame, keyed by the remote connection id (worker thread).</summary>
    public event Action<string, VideoFrame>? RemoteVideoFrame;

    public WebRtcManager(MeetingHubClient hub)
    {
        _hub = hub;
        _hub.SignalReceived += signal => _ = HandleSignalAsync(signal.FromConnectionId, signal.Type, signal.Payload);
    }

    // ---- Local devices ----------------------------------------------------

    public async Task StartMicrophoneAsync()
    {
        if (_micStarted) return;
        // Source only; playback of each remote peer uses its own sink endpoint.
        _micSource = new WindowsAudioEndPoint(new AudioEncoder(), -1, -1, disableSource: false, disableSink: true);
        await _micSource.StartAudio();
        _micStarted = true;
    }

    public async Task StartCameraAsync()
    {
        if (_cameraStarted) return;
        _cameraSource = new WindowsVideoEndPoint(new VpxVideoEncoder());
        _cameraSource.OnVideoSourceRawSample += (duration, width, height, sample, pixelFormat) =>
            LocalVideoFrame?.Invoke(new VideoFrame(sample, width, height, pixelFormat));
        await _cameraSource.StartVideo();
        // Begin encoding immediately. Without a source format set, the endpoint only emits raw
        // samples (self-view) and never the encoded samples we fan out to peers — so a peer that
        // negotiates late (or whose OnVideoFormatsNegotiated is missed) would receive no video.
        var formats = _cameraSource.GetVideoSourceFormats();
        if (formats.Count > 0)
            _cameraSource.SetVideoSourceFormat(formats[0]);
        _cameraStarted = true;
    }

    public void SetMicEnabled(bool enabled)
    {
        if (_micSource is null) return;
        if (enabled) _ = _micSource.ResumeAudio();
        else _ = _micSource.PauseAudio();
    }

    public void SetCameraEnabled(bool enabled)
    {
        if (_cameraSource is null) return;
        if (enabled) _ = _cameraSource.ResumeVideo();
        else _ = _cameraSource.PauseVideo();
    }

    // ---- Peer lifecycle ---------------------------------------------------

    /// <summary>Called by the newly-joined client for every participant already in the meeting.</summary>
    public async Task ConnectToPeerAsync(string remoteConnectionId)
    {
        var peer = CreatePeer(remoteConnectionId);
        var offer = peer.Pc.createOffer();
        await peer.Pc.setLocalDescription(offer);
        await _hub.SendSignalAsync(remoteConnectionId, "offer", JsonSerializer.Serialize(new SdpEnvelope("offer", offer.sdp)));
    }

    public async Task DisconnectPeerAsync(string remoteConnectionId)
    {
        if (_peers.TryRemove(remoteConnectionId, out var peer))
        {
            await peer.CloseAsync(_micSource, _cameraSource);
        }
    }

    private async Task HandleSignalAsync(string fromConnectionId, string type, string payload)
    {
        try
        {
            switch (type)
            {
                case "offer":
                {
                    var sdp = JsonSerializer.Deserialize<SdpEnvelope>(payload)!;
                    var peer = CreatePeer(fromConnectionId);
                    peer.Pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = sdp.Sdp });
                    var answer = peer.Pc.createAnswer();
                    await peer.Pc.setLocalDescription(answer);
                    await _hub.SendSignalAsync(fromConnectionId, "answer", JsonSerializer.Serialize(new SdpEnvelope("answer", answer.sdp)));
                    break;
                }
                case "answer":
                {
                    var sdp = JsonSerializer.Deserialize<SdpEnvelope>(payload)!;
                    if (_peers.TryGetValue(fromConnectionId, out var peer))
                        peer.Pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = sdp.Sdp });
                    break;
                }
                case "ice":
                {
                    var ice = JsonSerializer.Deserialize<IceEnvelope>(payload)!;
                    if (_peers.TryGetValue(fromConnectionId, out var peer))
                    {
                        peer.Pc.addIceCandidate(new RTCIceCandidateInit
                        {
                            candidate = ice.Candidate,
                            sdpMid = ice.SdpMid,
                            sdpMLineIndex = ice.SdpMLineIndex,
                        });
                    }
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Signal handling failed ({type} from {fromConnectionId}): {ex.Message}");
        }
    }

    private Peer CreatePeer(string remoteConnectionId)
    {
        // Re-offer/renegotiation from the same peer replaces the old connection.
        if (_peers.TryRemove(remoteConnectionId, out var stale))
        {
            _ = stale.CloseAsync(_micSource, _cameraSource);
        }

        var pc = new RTCPeerConnection(Config);
        var peer = new Peer(remoteConnectionId, pc);

        // Per-peer speaker sink so each remote participant's audio plays back.
        peer.SpeakerSink = new WindowsAudioEndPoint(new AudioEncoder(), -1, -1, disableSource: true, disableSink: false);
        // Per-peer decoder that turns incoming VP8 into raw frames for rendering.
        peer.VideoSink = new VideoEncoderEndPoint();
        peer.VideoSink.OnVideoSinkDecodedSample += (sample, width, height, stride, pixelFormat) =>
            RemoteVideoFrame?.Invoke(remoteConnectionId, new VideoFrame(sample, (int)width, (int)height, pixelFormat));

        if (_micSource is not null)
        {
            pc.addTrack(new MediaStreamTrack(_micSource.GetAudioSourceFormats(), MediaStreamStatusEnum.SendRecv));
        }
        if (_cameraSource is not null)
        {
            pc.addTrack(new MediaStreamTrack(_cameraSource.GetVideoSourceFormats(), MediaStreamStatusEnum.SendRecv));
        }

        pc.OnAudioFormatsNegotiated += formats =>
        {
            var format = formats.First();
            _micSource?.SetAudioSourceFormat(format);
            peer.SpeakerSink?.SetAudioSinkFormat(format);
        };
        pc.OnVideoFormatsNegotiated += formats =>
        {
            var format = formats.First();
            _cameraSource?.SetVideoSourceFormat(format);
            peer.VideoSink?.SetVideoSinkFormat(format);
        };

        // Fan the shared encoded media out to this peer connection.
        peer.AudioSampleHandler = (durationRtpUnits, sample) =>
            pc.SendAudio(durationRtpUnits, sample);
        peer.VideoSampleHandler = (durationRtpUnits, sample) =>
            pc.SendVideo(durationRtpUnits, sample);
        if (_micSource is not null) _micSource.OnAudioSourceEncodedSample += peer.AudioSampleHandler;
        if (_cameraSource is not null) _cameraSource.OnVideoSourceEncodedSample += peer.VideoSampleHandler;

        pc.OnAudioFrameReceived += frame => peer.SpeakerSink?.GotEncodedMediaFrame(frame);
        pc.OnVideoFrameReceived += (rep, timestamp, frame, format) =>
            peer.VideoSink?.GotVideoFrame(rep, timestamp, frame, format);

        pc.onicecandidate += candidate =>
        {
            if (candidate is null) return;
            var envelope = new IceEnvelope(candidate.candidate ?? "", candidate.sdpMid, (ushort)candidate.sdpMLineIndex);
            _ = _hub.SendSignalAsync(remoteConnectionId, "ice", JsonSerializer.Serialize(envelope));
        };

        pc.onconnectionstatechange += state =>
        {
            if (state == RTCPeerConnectionState.connected)
            {
                _ = peer.SpeakerSink?.StartAudioSink();
                // Push a keyframe so the new peer's decoder can render video immediately rather
                // than waiting for the encoder's next periodic keyframe.
                try { _cameraSource?.ForceKeyFrame(); } catch { /* not fatal */ }
            }
            else if (state is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed)
            {
                _ = DisconnectPeerAsync(remoteConnectionId);
            }
        };

        _peers[remoteConnectionId] = peer;
        return peer;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var key in _peers.Keys.ToList())
        {
            await DisconnectPeerAsync(key);
        }
        if (_micSource is not null) await _micSource.CloseAudio();
        if (_cameraSource is not null) await _cameraSource.CloseVideo();
    }

    private record SdpEnvelope(string Type, string Sdp);
    private record IceEnvelope(string Candidate, string? SdpMid, ushort SdpMLineIndex);

    /// <summary>State for one remote participant's peer connection.</summary>
    private class Peer(string remoteConnectionId, RTCPeerConnection pc)
    {
        public string RemoteConnectionId { get; } = remoteConnectionId;
        public RTCPeerConnection Pc { get; } = pc;
        public WindowsAudioEndPoint? SpeakerSink { get; set; }
        public VideoEncoderEndPoint? VideoSink { get; set; }
        public EncodedSampleDelegate? AudioSampleHandler { get; set; }
        public EncodedSampleDelegate? VideoSampleHandler { get; set; }

        public async Task CloseAsync(WindowsAudioEndPoint? micSource, WindowsVideoEndPoint? cameraSource)
        {
            if (AudioSampleHandler is not null && micSource is not null)
                micSource.OnAudioSourceEncodedSample -= AudioSampleHandler;
            if (VideoSampleHandler is not null && cameraSource is not null)
                cameraSource.OnVideoSourceEncodedSample -= VideoSampleHandler;

            try { Pc.close(); } catch { /* already closed */ }
            if (SpeakerSink is not null)
            {
                try { await SpeakerSink.CloseAudioSink(); } catch { /* device already released */ }
            }
        }
    }
}
