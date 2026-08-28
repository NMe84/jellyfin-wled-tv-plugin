using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
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
/// for anyone else.
///
/// LED timing is slaved to the TV's reported playback clock rather than free-running
/// on the decoder: each decoded frame carries its own content timestamp, and a clock
/// model — re-anchored on every progress event so it cannot drift — decides when to
/// release it.  A configurable display-latency offset then compensates for the fixed
/// lag between the TV's playback clock and its panel.
/// </summary>
public sealed class WledSamplingService : IHostedService, IDisposable
{
    // A reported position this far from the model is treated as a seek (re-decode);
    // anything smaller is a small drift and gently folded into the clock model.
    private const double SeekThresholdSeconds = 1.5;

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
        catch { /* streams unavailable */ }
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

            if (_pipeline.IsPaused)
                _pipeline.Resume(posSec);           // resuming from pause
            else
                _pipeline.UpdateClock(posSec, SeekThresholdSeconds); // keep the clock honest
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

        double fps = 24.0;
        if (vstream.RealFrameRate is > 0f) fps = vstream.RealFrameRate.Value;
        else if (vstream.AverageFrameRate is > 0f) fps = vstream.AverageFrameRate.Value;

        // Sample resolution: at least one pixel per LED per axis, plus headroom for
        // the edge-depth sample, bounded for performance; proportional to the video.
        int sh = Math.Max(Math.Max(vCount, (int)Math.Ceiling(hCount / ar)), 120);
        sh = Math.Min(sh, 480);
        int sw = (int)Math.Round(sh * ar);
        if (sw < hCount) sw = hCount;
        if ((sw & 1) == 1) sw++;
        if ((sh & 1) == 1) sh++;

        double delaySec = Math.Max(0, cfg.DisplayLatencyMs) / 1000.0;

        var pipeline = new Pipeline(
            _logger, _encoder.EncoderPath, cfg.WledWsUrl,
            hCount, vCount, cfg.LoopStart, cfg.Direction,
            cfg.DetectLetterbox, cfg.DetectPillarbox, cfg.BatchUpdates, cfg.Brightness,
            path, sw, sh, hdr, (double)hCount / vCount, fps, delaySec, sessionKey);

        _pipeline = pipeline;
        _logger.LogInformation(
            "WledTv: sampling '{Item}' on device {Device} at {Pos:0.0}s ({W}x{H} @ {Fps:0.##}fps{Hdr}, delay {Delay}ms)",
            item.Name, e.DeviceId, posSec, sw, sh, fps, hdr ? ", HDR→SDR" : string.Empty, cfg.DisplayLatencyMs);
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

    // ── One playback's decode → sample → schedule → send pipeline ─────────────
    private sealed class Pipeline
    {
        private readonly ILogger _logger;
        private readonly string _ffmpegPath;
        private readonly int _hCount, _vCount, _sampleW, _sampleH, _brightness, _maxBuffer;
        private readonly LedLoopStart _loopStart;
        private readonly LedLoopDirection _direction;
        private readonly bool _letterbox, _pillarbox, _batch, _hdr;
        private readonly double _panelAspect, _fps, _delaySec;
        private readonly string _path;
        private readonly WledConnection _wled;
        private readonly CancellationTokenSource _life = new();

        // Frame buffer, released on schedule by the sender.
        private readonly object _bufLock = new();
        private readonly Queue<FrameItem> _buffer = new();

        // TV clock model.
        private readonly object _clockLock = new();
        private double _anchorPos;      // content-seconds at the anchor instant
        private DateTime _anchorWall;   // wall time of the anchor
        private bool _paused;
        private double _pausedModel;    // frozen model value while paused

        private Task? _senderTask;
        private Process? _ff;
        private Task? _readTask;
        private CancellationTokenSource? _segCts;

        public string SessionKey { get; }
        public bool IsPaused { get; private set; }

        public Pipeline(
            ILogger logger, string ffmpegPath, string wledUrl,
            int hCount, int vCount, LedLoopStart loopStart, LedLoopDirection direction,
            bool letterbox, bool pillarbox, bool batch, int brightness,
            string path, int sampleW, int sampleH, bool hdr, double panelAspect,
            double fps, double delaySec, string sessionKey)
        {
            _logger = logger;
            _ffmpegPath = ffmpegPath;
            _wled = new WledConnection(wledUrl);
            _hCount = hCount; _vCount = vCount;
            _loopStart = loopStart; _direction = direction;
            _letterbox = letterbox; _pillarbox = pillarbox; _batch = batch; _brightness = brightness;
            _path = path; _sampleW = sampleW; _sampleH = sampleH; _hdr = hdr;
            _panelAspect = panelAspect; _fps = fps > 0 ? fps : 24.0; _delaySec = delaySec;
            SessionKey = sessionKey;
            _maxBuffer = Math.Max(60, (int)((delaySec + 5.0) * _fps));
        }

        public void Start(double posSec)
        {
            lock (_clockLock) { _paused = false; _anchorPos = posSec; _anchorWall = DateTime.UtcNow; }
            IsPaused = false;
            _senderTask = Task.Run(() => SenderLoopAsync(_life.Token));
            StartSegment(posSec);
        }

        public void Pause()
        {
            if (IsPaused) return;
            lock (_clockLock)
            {
                _pausedModel = _anchorPos + (DateTime.UtcNow - _anchorWall).TotalSeconds;
                _paused = true;
            }
            IsPaused = true;
            StopSegment();
            FlushBuffer();
        }

        public void Resume(double posSec)
        {
            lock (_clockLock) { _paused = false; _anchorPos = posSec; _anchorWall = DateTime.UtcNow; }
            IsPaused = false;
            FlushBuffer();
            StartSegment(posSec);
        }

        // Fold the reported position into the clock model.  A big gap is a seek and
        // triggers a re-decode; a small gap is drift and is eased in gently so the
        // release schedule never jumps.
        public void UpdateClock(double posSec, double seekThreshold)
        {
            bool reseek = false;
            lock (_clockLock)
            {
                if (_paused) return;
                double model = _anchorPos + (DateTime.UtcNow - _anchorWall).TotalSeconds;
                double err = posSec - model;
                if (Math.Abs(err) > seekThreshold)
                {
                    _anchorPos = posSec;
                    _anchorWall = DateTime.UtcNow;
                    reseek = true;
                }
                else
                {
                    _anchorPos = model + 0.25 * err; // ease 25% of the error per update
                    _anchorWall = DateTime.UtcNow;
                }
            }
            if (reseek)
            {
                FlushBuffer();
                StartSegment(posSec);
            }
        }

        public void Stop()
        {
            try { _life.Cancel(); } catch { /* ignore */ }
            StopSegment();
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

        private double TvClockNow()
        {
            lock (_clockLock)
            {
                return _paused ? _pausedModel : _anchorPos + (DateTime.UtcNow - _anchorWall).TotalSeconds;
            }
        }

        private void FlushBuffer()
        {
            lock (_bufLock) { _buffer.Clear(); }
        }

        private void StartSegment(double contentStartSec)
        {
            StopSegment();
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
            _segCts = cts;

            Process ff;
            try { ff = LaunchFfmpeg(contentStartSec); }
            catch (Exception ex) { _logger.LogError(ex, "WledTv: failed to launch ffmpeg"); return; }
            _ff = ff;
            _readTask = Task.Run(() => ReadLoopAsync(ff, contentStartSec, cts.Token));
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
            _ = Task.Run(async () =>
            {
                try { await ff.StandardError.ReadToEndAsync().ConfigureAwait(false); }
                catch { /* ignore */ }
            });
            return ff;
        }

        private async Task ReadLoopAsync(Process ff, double contentStartSec, CancellationToken ct)
        {
            int frameSize = _sampleW * _sampleH * 3;
            var buf = new byte[frameSize];
            var stream = ff.StandardOutput.BaseStream;
            long frameIndex = 0;
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

                    double contentSec = contentStartSec + frameIndex / _fps;
                    frameIndex++;

                    var colors = EdgeSampler.Compute(
                        buf, _sampleW, _sampleH, _hCount, _vCount,
                        _loopStart, _direction, _letterbox, _pillarbox, _panelAspect);

                    lock (_bufLock)
                    {
                        _buffer.Enqueue(new FrameItem(contentSec, colors));
                        while (_buffer.Count > _maxBuffer) _buffer.Dequeue();
                    }
                }
            }
            catch (OperationCanceledException) { /* segment stopped */ }
            catch (Exception ex) { _logger.LogDebug(ex, "WledTv: frame read loop ended"); }
        }

        private async Task SenderLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                double tv = TvClockNow();
                bool hasDue = false;
                FrameItem due = default;
                int waitMs = 200;

                lock (_bufLock)
                {
                    // Release every frame whose show-time has arrived, keeping only the
                    // newest (drop stale ones if we ever fall behind).
                    while (_buffer.Count > 0 && tv >= _buffer.Peek().ContentSec + _delaySec)
                    {
                        due = _buffer.Dequeue();
                        hasDue = true;
                    }
                    if (!hasDue && _buffer.Count > 0)
                    {
                        double deltaSec = (_buffer.Peek().ContentSec + _delaySec) - tv;
                        waitMs = (int)Math.Clamp(deltaSec * 1000.0, 2.0, 200.0);
                    }
                }

                if (hasDue)
                {
                    if (!_wled.IsOpen)
                    {
                        bool ok = await _wled.EnsureConnectedAsync(ct).ConfigureAwait(false);
                        if (!ok)
                        {
                            try { await Task.Delay(1000, ct).ConfigureAwait(false); } catch { break; }
                            continue;
                        }
                    }
                    try
                    {
                        await _wled.SendColorsAsync(due.Colors, _brightness, _batch, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "WledTv: send failed, will reconnect");
                        _wled.Dispose();
                    }
                }
                else
                {
                    try { await Task.Delay(waitMs, ct).ConfigureAwait(false); } catch { break; }
                }
            }
        }

        private readonly struct FrameItem
        {
            public readonly double ContentSec;
            public readonly byte[] Colors;
            public FrameItem(double contentSec, byte[] colors)
            {
                ContentSec = contentSec;
                Colors = colors;
            }
        }
    }
}
