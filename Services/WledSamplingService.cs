using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WledTv.Services;

/// <summary>
/// Server-side edge-lighting.  For the configured client device the server decodes
/// the playing video with the bundled ffmpeg (hardware-accelerated, tone-mapped for
/// HDR), samples the edges of every frame, and streams the colours to WLED over a
/// WebSocket.  Only the selected device is sampled, so no extra decode is incurred
/// for anyone else.  Playback position, pause and seek are followed via the session
/// events so the LEDs stay matched to what is on screen.
/// </summary>
public sealed class WledSamplingService : IHostedService, IDisposable
{
    // How far the decoder position may drift from the reported playback position
    // before we re-seek ffmpeg to resynchronise.  Kept generous so a small, stable
    // start-up offset never causes constant re-seeking; large jumps (user seeks)
    // and pause/resume are still caught.  Real-time (-re) decode keeps the steady
    // state tight without re-seeking.
    private const double DriftResyncSeconds = 2.0;

    private readonly ISessionManager _sessions;
    private readonly IMediaEncoder _encoder;
    private readonly ILogger<WledSamplingService> _logger;

    private readonly object _lock = new();
    private Pipeline? _pipeline;
    private bool _disposed;

    public WledSamplingService(
        ISessionManager sessions,
        IMediaEncoder encoder,
        ILogger<WledSamplingService> logger)
    {
        _sessions = sessions;
        _encoder = encoder;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessions.PlaybackStart += OnPlaybackProgress;
        _sessions.PlaybackProgress += OnPlaybackProgress;
        _sessions.PlaybackStopped += OnPlaybackStopped;
        _logger.LogInformation("WledTv: server-side sampling service started");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessions.PlaybackStart -= OnPlaybackProgress;
        _sessions.PlaybackProgress -= OnPlaybackProgress;
        _sessions.PlaybackStopped -= OnPlaybackStopped;
        StopPipeline();
        return Task.CompletedTask;
    }

    private void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e)
    {
        try { HandleProgress(e); }
        catch (Exception ex) { _logger.LogError(ex, "WledTv: error handling playback progress"); }
    }

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null) return;
        if (!DeviceMatches(cfg, e.DeviceId)) return;
        StopPipeline();
    }

    private void HandleProgress(PlaybackProgressEventArgs e)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null) return;

        if (!cfg.Enabled)
        {
            StopPipeline();
            return;
        }

        if (!DeviceMatches(cfg, e.DeviceId))
            return;

        var item = e.Item;
        var path = item?.Path;
        if (item is null || string.IsNullOrEmpty(path))
            return;

        // Only sample real video items (skip music etc.).
        MediaStream? vstream = null;
        try
        {
            vstream = item.GetMediaStreams()?.FirstOrDefault(s => s.Type == MediaStreamType.Video);
        }
        catch { /* streams unavailable — fall through with defaults */ }
        if (vstream is null)
            return;

        double posSec = (e.PlaybackPositionTicks ?? 0) / (double)TimeSpan.TicksPerSecond;
        string sessionKey = !string.IsNullOrEmpty(e.PlaySessionId)
            ? e.PlaySessionId!
            : (e.DeviceId + "|" + path);

        lock (_lock)
        {
            if (e.IsPaused)
            {
                if (_pipeline?.SessionKey == sessionKey)
                    _pipeline.Pause();
                return;
            }

            if (_pipeline is null || _pipeline.SessionKey != sessionKey)
            {
                StartPipeline(cfg, e, item, path!, vstream, posSec, sessionKey);
                return;
            }

            // Same session, playing: resume from pause or resync on drift/seek.
            if (_pipeline.IsPaused)
            {
                _pipeline.ResumeAt(posSec);
            }
            else if (Math.Abs(posSec - _pipeline.EstimatedPositionSeconds) > DriftResyncSeconds)
            {
                _pipeline.ResumeAt(posSec);
            }
        }
    }

    private void StartPipeline(
        PluginConfiguration cfg, PlaybackProgressEventArgs e, BaseItem item, string path,
        MediaStream vstream, double posSec, string sessionKey)
    {
        StopPipeline();

        int hCount = Math.Max(1, cfg.HorizontalLedCount);
        int vCount = Math.Max(1, cfg.VerticalLedCount);

        int vw = vstream.Width ?? 0;
        int vh = vstream.Height ?? 0;
        double ar = vh > 0 ? (double)vw / vh : 16.0 / 9.0;
        bool hdr = IsHdr(vstream);

        // Sample resolution: at least one pixel per LED on each axis, plus headroom
        // for the edge-depth sample, bounded for performance.  Proportional to the
        // video so bar detection / aspect maths stay correct.
        int sh = Math.Max(Math.Max(vCount, (int)Math.Ceiling(hCount / ar)), 120);
        sh = Math.Min(sh, 480);
        int sw = (int)Math.Round(sh * ar);
        if (sw < hCount) sw = hCount;
        if ((sw & 1) == 1) sw++;
        if ((sh & 1) == 1) sh++;

        var pipeline = new Pipeline(
            _logger, _encoder.EncoderPath, cfg.WledWsUrl,
            hCount, vCount, cfg.LoopStart, cfg.Direction,
            cfg.DetectLetterbox, cfg.DetectPillarbox, cfg.BatchUpdates, cfg.Brightness,
            path, sw, sh, hdr, (double)hCount / vCount, sessionKey);

        _pipeline = pipeline;
        _logger.LogInformation(
            "WledTv: sampling '{Item}' on device {Device} at {Pos:0.0}s ({W}x{H}{Hdr})",
            item.Name, e.DeviceId, posSec, sw, sh, hdr ? ", HDR→SDR" : string.Empty);
        pipeline.Start(posSec);
    }

    private void StopPipeline()
    {
        Pipeline? p;
        lock (_lock)
        {
            p = _pipeline;
            _pipeline = null;
        }
        p?.Stop();
    }

    private static bool DeviceMatches(PluginConfiguration cfg, string? deviceId) =>
        string.IsNullOrEmpty(cfg.DeviceId) ||
        string.Equals(cfg.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase);

    private static bool IsHdr(MediaStream v) =>
        string.Equals(v.ColorTransfer, "smpte2084", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(v.ColorTransfer, "arib-std-b67", StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopPipeline();
        GC.SuppressFinalize(this);
    }

    // ── One playback's decode → sample → send pipeline ────────────────────────
    private sealed class Pipeline
    {
        private readonly ILogger _logger;
        private readonly string _ffmpegPath;
        private readonly int _hCount, _vCount, _sampleW, _sampleH, _brightness;
        private readonly LedLoopStart _loopStart;
        private readonly LedLoopDirection _direction;
        private readonly bool _letterbox, _pillarbox, _batch, _hdr;
        private readonly double _panelAspect;
        private readonly string _path;
        private readonly WledConnection _wled;
        private readonly Channel<byte[]> _channel;
        private readonly CancellationTokenSource _life = new();

        private Task? _senderTask;
        private Process? _ff;
        private Task? _readTask;
        private CancellationTokenSource? _segCts;

        private double _segStartSec;
        private DateTime _segStartUtc;
        private double _pausedSec;

        public string SessionKey { get; }
        public bool IsPaused { get; private set; }

        public double EstimatedPositionSeconds =>
            IsPaused ? _pausedSec : _segStartSec + (DateTime.UtcNow - _segStartUtc).TotalSeconds;

        public Pipeline(
            ILogger logger, string ffmpegPath, string wledUrl,
            int hCount, int vCount, LedLoopStart loopStart, LedLoopDirection direction,
            bool letterbox, bool pillarbox, bool batch, int brightness,
            string path, int sampleW, int sampleH, bool hdr, double panelAspect, string sessionKey)
        {
            _logger = logger;
            _ffmpegPath = ffmpegPath;
            _wled = new WledConnection(wledUrl);
            _hCount = hCount; _vCount = vCount;
            _loopStart = loopStart; _direction = direction;
            _letterbox = letterbox; _pillarbox = pillarbox; _batch = batch; _brightness = brightness;
            _path = path; _sampleW = sampleW; _sampleH = sampleH; _hdr = hdr;
            _panelAspect = panelAspect;
            SessionKey = sessionKey;
            _channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true
            });
        }

        public void Start(double posSec)
        {
            _senderTask = Task.Run(() => SenderLoopAsync(_life.Token));
            StartSegment(posSec);
        }

        public void Pause()
        {
            if (IsPaused) return;
            _pausedSec = EstimatedPositionSeconds;
            IsPaused = true;
            StopSegment();
        }

        public void ResumeAt(double posSec)
        {
            IsPaused = false;
            StartSegment(posSec);
        }

        public void Stop()
        {
            try { _life.Cancel(); } catch { /* ignore */ }
            StopSegment();
            // Turn the strip off and close the socket on a background task.
            Task.Run(async () =>
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await _wled.SendOffAsync(cts.Token).ConfigureAwait(false);
                }
                catch { /* best effort */ }
                finally { _wled.Dispose(); }
            });
        }

        private void StartSegment(double posSec)
        {
            StopSegment();
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
            _segCts = cts;
            _segStartSec = posSec;
            _segStartUtc = DateTime.UtcNow;

            Process ff;
            try
            {
                ff = LaunchFfmpeg(posSec);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WledTv: failed to launch ffmpeg");
                return;
            }
            _ff = ff;
            _readTask = Task.Run(() => ReadLoopAsync(ff, cts.Token));
        }

        private void StopSegment()
        {
            var cts = _segCts;
            _segCts = null;
            var ff = _ff;
            _ff = null;
            try { cts?.Cancel(); } catch { /* ignore */ }
            try { cts?.Dispose(); } catch { /* ignore */ }
            if (ff != null)
            {
                try { if (!ff.HasExited) ff.Kill(true); } catch { /* ignore */ }
                try { ff.Dispose(); } catch { /* ignore */ }
            }
        }

        private Process LaunchFfmpeg(double posSec)
        {
            string filter = _hdr
                ? $"zscale=w={_sampleW}:h={_sampleH}:t=linear:npl=100,tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:p=bt709:r=tv,format=rgb24"
                : $"scale={_sampleW}:{_sampleH}:flags=bilinear,format=rgb24";

            var psi = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            void Arg(string a) => psi.ArgumentList.Add(a);
            Arg("-nostdin");
            Arg("-hide_banner");
            Arg("-loglevel"); Arg("error");
            Arg("-hwaccel"); Arg("auto");
            Arg("-re");
            Arg("-ss"); Arg(posSec.ToString("0.###", CultureInfo.InvariantCulture));
            Arg("-i"); Arg(_path);
            Arg("-an"); Arg("-sn");
            Arg("-vf"); Arg(filter);
            Arg("-f"); Arg("rawvideo");
            Arg("-pix_fmt"); Arg("rgb24");
            Arg("pipe:1");

            var ff = new Process { StartInfo = psi };
            ff.Start();
            // Drain stderr so a full pipe never deadlocks ffmpeg.
            _ = Task.Run(async () =>
            {
                try { await ff.StandardError.ReadToEndAsync().ConfigureAwait(false); }
                catch { /* ignore */ }
            });
            return ff;
        }

        private async Task ReadLoopAsync(Process ff, CancellationToken ct)
        {
            int frameSize = _sampleW * _sampleH * 3;
            var buf = new byte[frameSize];
            var stream = ff.StandardOutput.BaseStream;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    int read = 0;
                    while (read < frameSize)
                    {
                        int n = await stream.ReadAsync(buf.AsMemory(read, frameSize - read), ct).ConfigureAwait(false);
                        if (n <= 0) return; // EOF / process ended
                        read += n;
                    }

                    var colors = EdgeSampler.Compute(
                        buf, _sampleW, _sampleH, _hCount, _vCount,
                        _loopStart, _direction, _letterbox, _pillarbox, _panelAspect);
                    _channel.Writer.TryWrite(colors);
                }
            }
            catch (OperationCanceledException) { /* segment stopped */ }
            catch (Exception ex) { _logger.LogDebug(ex, "WledTv: frame read loop ended"); }
        }

        private async Task SenderLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                byte[] colors;
                try { colors = await _channel.Reader.ReadAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch { break; }

                if (!_wled.IsOpen)
                {
                    bool ok = await _wled.EnsureConnectedAsync(ct).ConfigureAwait(false);
                    if (!ok)
                    {
                        try { await Task.Delay(1000, ct).ConfigureAwait(false); }
                        catch { break; }
                        continue;
                    }
                }

                try
                {
                    await _wled.SendColorsAsync(colors, _brightness, _batch, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "WledTv: send failed, will reconnect");
                    _wled.Dispose();
                }
            }
        }
    }
}
